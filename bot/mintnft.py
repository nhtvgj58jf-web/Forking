"""/mint — выдача NFT админу кнопками: подарок -> модель -> узор -> фон -> проверка -> выдача.
Работает через /root/mint.js (docker exec mongosh). Только для ADMIN_IDS.
Подключение в bot.py:  import mintnft; mintnft.register(dp)   (сразу после admin.register(dp))
"""
import asyncio
import json
import os
import re
from asyncio.subprocess import PIPE, STDOUT

from aiogram import F
from aiogram.filters import Command, CommandObject
from aiogram.types import CallbackQuery, InlineKeyboardButton, InlineKeyboardMarkup, Message

ADMIN_IDS = {int(x) for x in os.environ.get("ADMIN_IDS", "8746843031").split(",") if x.strip()}
MONGO = os.environ.get("MONGO_CONTAINER", "compose-mongodb-1")
QUERY_SERVER = os.environ.get("QUERY_SERVER_CONTAINER", "compose-messenger-query-server-1")
MINT_JS = os.environ.get("MINT_JS", "/root/mint.js")
OWNER_DEFAULT = int(os.environ.get("MINT_OWNER", "2010005"))
PAGE = 8
STEPS = ["gift", "model", "pattern", "backdrop"]
TITLES = {"gift": "подарок", "model": "модель", "pattern": "узор", "backdrop": "фон"}
STATE = {}
LOCK = asyncio.Lock()


async def run(args, stdin=None, timeout=120):
    p = await asyncio.create_subprocess_exec(
        *args, stdin=PIPE if stdin is not None else None, stdout=PIPE, stderr=STDOUT)
    try:
        out, _ = await asyncio.wait_for(p.communicate(stdin), timeout)
    except asyncio.TimeoutError:
        p.kill()
        return 1, "timeout"
    return p.returncode, out.decode(errors="replace").strip()


async def mongo_json(js):
    code, out = await run(["docker", "exec", "-i", MONGO, "mongosh", "tg", "--quiet", "--eval", js])
    if code != 0:
        raise RuntimeError(out[-300:])
    return json.loads(out.strip().splitlines()[-1])


async def load_items(step, st):
    if step == "gift":
        js = ('print(JSON.stringify(db.getCollection("unique-star-gifts").aggregate('
              '[{$group:{_id:"$GiftId",t:{$first:"$Title"}}},{$sort:{_id:1}}]).toArray()'
              '.map(x=>({id:Number(x._id),n:x.t}))))')
        rows = await mongo_json(js)
        return [{"id": r["id"], "label": "%s (%s)" % (r["n"], r["id"])} for r in rows]
    js = ('const g=%d; print(JSON.stringify(db.getCollection("star-gift-upgrade-config")'
          '.find({Type:"%s",GiftId:{$in:[g,0]}}).toArray()'
          '.map(r=>({id:r._id.toString(),n:r.Name,r:Number(r.RarityPermille),own:Number(r.GiftId)===g}))))'
          % (st["gift"]["id"], step))
    rows = await mongo_json(js)
    rows.sort(key=lambda r: (not r["own"], r["r"]))
    return [{"id": r["id"], "label": "%s · %s‰" % (r["n"], r["r"])} for r in rows]


def summary(st):
    parts = []
    for s in STEPS:
        if st.get(s):
            parts.append("%s: %s" % (TITLES[s], st[s]["label"]))
    return "\n".join(parts)


def keyboard(st, page):
    items = st["items"]
    pages = max(1, (len(items) + PAGE - 1) // PAGE)
    page = max(0, min(page, pages - 1))
    st["page"] = page
    rows = []
    chunk = list(enumerate(items))[page * PAGE:(page + 1) * PAGE]
    for i in range(0, len(chunk), 2):
        rows.append([InlineKeyboardButton(text=l["label"][:40], callback_data="mt:pk:%d" % idx)
                     for idx, l in chunk[i:i + 2]])
    nav = []
    if page > 0:
        nav.append(InlineKeyboardButton(text="◀", callback_data="mt:pg:%d" % (page - 1)))
    nav.append(InlineKeyboardButton(text="%d/%d" % (page + 1, pages), callback_data="mt:noop"))
    if page < pages - 1:
        nav.append(InlineKeyboardButton(text="▶", callback_data="mt:pg:%d" % (page + 1)))
    rows.append(nav)
    rows.append([InlineKeyboardButton(text="✖ Отмена", callback_data="mt:no")])
    return InlineKeyboardMarkup(inline_keyboard=rows)


def list_text(st):
    head = summary(st)
    t = "Выберите %s (всего %d). Напишите часть названия для поиска." % (TITLES[st["step"]], len(st["items"]))
    return (head + "\n\n" if head else "") + t


async def safe_edit(msg, text, kb=None):
    try:
        await msg.edit_text(text, reply_markup=kb)
    except Exception:
        pass


def env_args(st, apply):
    a = ["docker", "exec", "-i"]
    for k, v in (("GIFT", st["gift"]["id"]), ("OWNER", st["owner"]), ("MODEL_ID", st["model"]["id"]),
                 ("PATTERN_ID", st["pattern"]["id"]), ("BACKDROP_ID", st["backdrop"]["id"])):
        a += ["-e", "%s=%s" % (k, v)]
    if apply:
        a += ["-e", "APPLY=1"]
    return a + [MONGO, "mongosh", "tg", "--quiet"]


async def run_mint(st, apply):
    with open(MINT_JS, "rb") as f:
        js = f.read()
    return await run(env_args(st, apply), stdin=js)


async def cmd_mint(m: Message, command: CommandObject):
    owner = OWNER_DEFAULT
    arg = (command.args or "").strip()
    if arg:
        if not re.fullmatch(r"\d{1,12}", arg):
            return await m.answer("/mint [id получателя в Крикграме]", parse_mode=None)
        owner = int(arg)
    st = {"step": "gift", "owner": owner}
    try:
        st["full"] = await load_items("gift", st)
    except Exception as e:
        return await m.answer("Не удалось получить список: %s" % e, parse_mode=None)
    st["items"] = st["full"]
    STATE[m.from_user.id] = st
    await m.answer("Получатель: %d\n\n%s" % (owner, list_text(st)), reply_markup=keyboard(st, 0), parse_mode=None)


async def on_text(m: Message):
    st = STATE.get(m.from_user.id)
    q = m.text.strip().lower()
    items = [x for x in st["full"] if q in x["label"].lower()]
    if not items:
        return await m.answer("Ничего не найдено по «%s». Напишите другое слово." % m.text, parse_mode=None)
    st["items"] = items
    await m.answer(list_text(st), reply_markup=keyboard(st, 0), parse_mode=None)


def searching(m: Message):
    st = STATE.get(m.from_user.id)
    return bool(st) and st.get("step") in STEPS


async def on_cb(cb: CallbackQuery):
    uid = cb.from_user.id
    parts = cb.data.split(":")
    act = parts[1]
    st = STATE.get(uid)
    if act == "noop":
        return await cb.answer()
    if act == "no" or not st:
        STATE.pop(uid, None)
        await cb.answer("Отменено" if act == "no" else "Сессия устарела, /mint заново")
        return await safe_edit(cb.message, "Отменено.")
    if act == "pg" and st["step"] in STEPS:
        await cb.answer()
        return await safe_edit(cb.message, list_text(st), keyboard(st, int(parts[2])))
    if act == "pk" and st["step"] in STEPS:
        idx = int(parts[2])
        if idx >= len(st["items"]):
            return await cb.answer("Список устарел")
        st[st["step"]] = st["items"][idx]
        nxt = STEPS.index(st["step"]) + 1
        await cb.answer()
        if nxt < len(STEPS):
            st["step"] = STEPS[nxt]
            try:
                st["full"] = await load_items(st["step"], st)
            except Exception as e:
                return await safe_edit(cb.message, "Ошибка списка: %s" % e)
            st["items"] = st["full"]
            return await safe_edit(cb.message, list_text(st), keyboard(st, 0))
        st["step"] = "confirm"
        async with LOCK:
            code, out = await run_mint(st, apply=False)
        kb = InlineKeyboardMarkup(inline_keyboard=[[
            InlineKeyboardButton(text="✅ Выдать", callback_data="mt:ok"),
            InlineKeyboardButton(text="✖ Отмена", callback_data="mt:no")]])
        if code != 0 or "Готово к выпуску" not in out:
            STATE.pop(uid, None)
            return await safe_edit(cb.message, "Проверка не прошла:\n%s" % out[-1500:])
        return await safe_edit(cb.message, "Проверка (ничего не записано):\n%s" % out[-1500:], kb)
    if act == "ok" and st["step"] == "confirm":
        st["step"] = "done"
        await cb.answer("Выдаю…")
        await safe_edit(cb.message, "Выдаю…")
        async with LOCK:
            code, out = await run_mint(st, apply=True)
            ok = code == 0 and "Выпущено" in out
            extra = ""
            if ok:
                c2, o2 = await run(["docker", "restart", QUERY_SERVER], timeout=120)
                extra = "\n\nquery-server перезапущен." if c2 == 0 else "\n\nПерезапустите query-server вручную: %s" % o2[-200:]
        STATE.pop(uid, None)
        return await safe_edit(cb.message, ("Готово:\n" if ok else "Ошибка:\n") + out[-1500:] + extra)
    await cb.answer()


def register(dp):
    adm = F.from_user.id.in_(ADMIN_IDS)
    dp.message.register(cmd_mint, Command("mint"), adm)
    dp.callback_query.register(on_cb, F.data.startswith("mt:"), adm)
    dp.message.register(on_text, adm, F.text, ~F.text.startswith("/"), searching)
