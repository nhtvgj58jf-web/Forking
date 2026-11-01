import asyncio, os, random, re
from aiogram import F
from aiogram.filters import Command, CommandObject
from aiogram.types import Message

ADMIN_IDS = {int(x) for x in os.environ.get("ADMIN_IDS", "8746843031").split(",") if x.strip()}
SCRIPTS = os.environ.get("SCRIPTS_DIR", "/root/testgram-src/scripts")
MONGO = os.environ.get("MONGO_CONTAINER", "compose-mongodb-1")
HELP = ("/user <id|ник> - данные и звёзды\n/stars <id> <n> - выдать звёзды\n"
        "/nft <id> <ник> - NFT-юзернейм\n/nfts - список NFT-ников\n"
        "/newnum - выдать анонимный номер 888\n/allow <цифры> - разрешить номер\n"
        "/orders - открытые заявки\n/price <заявка> <звёзды> - выставить цену\n/decline <заявка> [причина] - отклонить")

async def run(*args, timeout=60):
    p = await asyncio.create_subprocess_exec(*args, stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.STDOUT)
    try:
        out, _ = await asyncio.wait_for(p.communicate(), timeout)
    except asyncio.TimeoutError:
        p.kill()
        return 1, "timeout"
    return p.returncode, out.decode(errors="replace").strip()

async def mongo(js):
    return await run("docker", "exec", "-i", MONGO, "mongosh", "tg", "--quiet", "--eval", js)

def tail(t, n=12):
    return "\n".join(t.splitlines()[-n:])[:3500]

async def reply(m: Message, text):
    await m.answer(text or "(пусто)", parse_mode=None)

async def cmd_help(m: Message):
    await reply(m, HELP)

async def cmd_user(m: Message, command: CommandObject):
    x = (command.args or "").strip().lstrip("@")
    if re.fullmatch(r"\d{1,12}", x):
        q = "{UserId: NumberLong('%s')}" % x
    elif re.fullmatch(r"[A-Za-z0-9_]{3,32}", x):
        q = "{$or:[{UserName:'%s'},{'Usernames.Username':'%s'}]}" % (x, x)
    else:
        return await reply(m, "/user <id или ник>")
    js = ("const u=db['eventflow-userreadmodel'].findOne(%s,{UserId:1,UserName:1,Usernames:1});"
          "if(!u){print('не найден');quit(0)}"
          "const b=db['star-balances'].findOne({UserId:u.UserId});"
          "print(JSON.stringify({user:u,stars:b?b.Balance:0},"
          "(k,v)=>v&&v._bsontype==='Long'?v.toString():v,1))") % q
    await reply(m, tail((await mongo(js))[1], 40))

async def cmd_stars(m: Message, command: CommandObject):
    a = (command.args or "").split()
    if len(a) != 2 or not re.fullmatch(r"\d{1,12}", a[0]) or not re.fullmatch(r"[1-9]\d{0,8}", a[1]):
        return await reply(m, "/stars <id> <количество>")
    await reply(m, tail((await run("bash", f"{SCRIPTS}/give-stars.sh", a[0], a[1]))[1]))

async def cmd_nft(m: Message, command: CommandObject):
    a = (command.args or "").lower().split()
    if len(a) != 2 or not re.fullmatch(r"\d{1,12}", a[0]) or not re.fullmatch(r"[a-z0-9_]{4,32}", a[1]):
        return await reply(m, "/nft <id> <ник> (a-z, 0-9, _)")
    await reply(m, tail((await run("bash", f"{SCRIPTS}/assign-nft-username.sh", *a))[1]))

async def cmd_nfts(m: Message):
    js = "db.fragment_collectibles.find({type:'username'}).forEach(d=>print(d.username))"
    await reply(m, tail((await mongo(js))[1], 60))

async def issue(n):
    return await mongo("db.allowed_phones.updateOne({_id:'%s'},{$set:{issued:new Date()}},{upsert:true})" % n)

async def cmd_newnum(m: Message):
    for _ in range(20):
        n = "888" + "%08d" % random.randint(0, 99999999)
        if (await mongo("db.allowed_phones.countDocuments({_id:'%s'})" % n))[1].strip() == "0":
            code, out = await issue(n)
            return await reply(m, "+%s выдан" % n if code == 0 else tail(out))
    await reply(m, "не удалось подобрать номер")

async def cmd_allow(m: Message, command: CommandObject):
    n = (command.args or "").strip().lstrip("+")
    if not re.fullmatch(r"\d{5,15}", n):
        return await reply(m, "/allow <номер цифрами>")
    code, out = await issue(n)
    await reply(m, "+%s разрешён" % n if code == 0 else tail(out))

def register(dp):
    adm = F.from_user.id.in_(ADMIN_IDS)
    for name, fn in (("help", cmd_help), ("user", cmd_user), ("stars", cmd_stars), ("nft", cmd_nft),
                     ("nfts", cmd_nfts), ("newnum", cmd_newnum), ("allow", cmd_allow)):
        dp.message.register(fn, Command(name), adm)
