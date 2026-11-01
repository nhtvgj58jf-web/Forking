"""
gift_rotate.py - модуль для бота (aiogram 3). Кладётся в /root/testgram-src/bot/
Подключение в bot.py (рядом с admin.register(dp)):
    import gift_rotate; gift_rotate.register(dp)
"""
import asyncio, json, os
from aiogram import Router, Bot
from aiogram.filters import Command
from aiogram.types import Message, CallbackQuery, InlineKeyboardMarkup, InlineKeyboardButton

router = Router()
COMPOSE = "/root/testgram-src/docker/compose"
BOT_DIR = "/root/testgram-src/bot"
CHECK_EVERY = 120
LOCK = asyncio.Lock()
notified = set()


def admins():
    return [int(x) for x in os.environ.get("ADMIN_IDS", "8746843031").replace(" ", "").split(",") if x]


async def core(*args, timeout=1800):
    os.makedirs("/root/rotate_out", exist_ok=True)
    cmd = ["docker", "run", "--rm", "--network", "compose_tg-net", "--env-file", COMPOSE + "/.env",
           "-v", BOT_DIR + "/gift_core.py:/app/gift_core.py:ro",
           "-v", "/root/tgdump2/fetch.py:/app/fetch.py:ro",
           "-v", BOT_DIR + "/reserve.json:/reserve.json:ro",
           "-v", "/root/rotate_out:/out",
           "local/giftpy", "python", "/app/gift_core.py", *[str(a) for a in args]]
    p = await asyncio.create_subprocess_exec(*cmd, stdout=asyncio.subprocess.PIPE,
                                             stderr=asyncio.subprocess.STDOUT)
    try:
        data, _ = await asyncio.wait_for(p.communicate(), timeout)
    except asyncio.TimeoutError:
        p.kill()
        return {"ok": False, "error": "таймаут"}
    txt = data.decode(errors="replace")
    for line in reversed(txt.splitlines()):
        if line.startswith("JSON:"):
            return json.loads(line[5:])
    return {"ok": False, "error": txt[-700:] or "пустой ответ"}


def kb(sold_id, cands):
    rows = [[InlineKeyboardButton(
        text="%s — %s⭐ / ул. %s / лимит %s" % (c.get("title") or c["slug"], c["stars"], c["upgrade"], c["total"]),
        callback_data="rot:%s:%s" % (sold_id, c["slug"]))] for c in cands[:10]]
    rows.append([InlineKeyboardButton(text="Пропустить", callback_data="rotskip:%s" % sold_id)])
    return InlineKeyboardMarkup(inline_keyboard=rows)


async def check(bot: Bot):
    r = await core("soldout", timeout=120)
    if "sold" not in r:
        return
    for g in r["sold"]:
        if g["GiftId"] in notified:
            continue
        notified.add(g["GiftId"])
        cands = r["cands"]
        text = "Закончился лимит: %s (GiftId %s)." % (g["Title"], g["GiftId"])
        text += "\nКакой поставить?" if cands else "\nРезерв пуст. Добавь коллекции в reserve.json и жми /rotate."
        for a in admins():
            await bot.send_message(a, text, reply_markup=kb(g["GiftId"], cands) if cands else None)


async def watcher(bot: Bot):
    while True:
        try:
            await check(bot)
        except Exception as e:
            print("gift_rotate watcher:", repr(e))
        await asyncio.sleep(CHECK_EVERY)


async def _startup(bot: Bot):
    asyncio.create_task(watcher(bot))


@router.message(Command("rotate"))
async def cmd_rotate(m: Message, bot: Bot):
    if m.from_user.id not in admins():
        return
    notified.clear()
    await m.answer("Проверяю…")
    await check(bot)


@router.message(Command("reserve"))
async def cmd_reserve(m: Message):
    if m.from_user.id not in admins():
        return
    try:
        res = json.load(open(BOT_DIR + "/reserve.json", encoding="utf-8"))
    except Exception as e:
        return await m.answer("reserve.json: %r" % e)
    await m.answer("\n".join("%s — %s⭐ / ул. %s / лимит %s" % (c["slug"], c["stars"], c["upgrade"], c["total"])
                             for c in res) or "пусто")


@router.callback_query(lambda c: c.data and c.data.startswith("rotskip:"))
async def skip(c: CallbackQuery):
    if c.from_user.id not in admins():
        return
    await c.message.edit_text(c.message.text + "\nПропущено.")
    await c.answer()


@router.callback_query(lambda c: c.data and c.data.startswith("rot:"))
async def pick(c: CallbackQuery):
    if c.from_user.id not in admins():
        return await c.answer()
    _, sold_id, slug = c.data.split(":", 2)
    await c.answer()
    if LOCK.locked():
        return await c.message.answer("Занято: уже идёт другая постановка.")
    cand = None
    try:
        for x in json.load(open(BOT_DIR + "/reserve.json", encoding="utf-8")):
            if x["slug"] == slug:
                cand = x
    except Exception:
        pass
    if not cand:
        return await c.message.answer("Нет %s в reserve.json" % slug)
    async with LOCK:
        await c.message.edit_text("Качаю %s с Fragment, режу, проверяю. 5-15 минут…" % slug)
        r = await core("prepare", slug, cand.get("title") or slug)
        if not r.get("ok"):
            lines = r.get("problems") or [r.get("error", "?")]
            return await c.message.answer("НЕ ПОСТАВЛЕНО (%s):\n- " % slug + "\n- ".join(lines[:15]))
        warn = ("\nПредупреждения:\n- " + "\n- ".join(r["warns"][:5])) if r.get("warns") else ""
        await c.message.answer("Проверка ок: моделей %s, узоров %s, фонов %s, обрезано %s.%s\nСтавлю в магазин…"
                               % (r["models"], r["patterns"], r["backdrops"], r["trimmed"], warn))
        r = await core("install", slug, sold_id, cand["stars"], cand["upgrade"], cand["total"], timeout=600)
        if not r.get("ok"):
            return await c.message.answer("Ошибка записи: %s" % (r.get("error") or r))
        p = await asyncio.create_subprocess_exec("docker", "compose", "restart", "messenger-query-server",
                                                 cwd=COMPOSE, stdout=asyncio.subprocess.DEVNULL,
                                                 stderr=asyncio.subprocess.DEVNULL)
        await p.wait()
        await c.message.answer(
            "Поставлен: %s (GiftId %s)\nЦена %s⭐, улучшение %s⭐, лимит %s\nМоделей %s, узоров %s, новых фонов %s\n"
            "В магазине пока модель NFT. Обычную анимацию добавь через real_gifts.py."
            % (r["title"], r["GiftId"], r["stars"], r["upgrade"], r["total"], r["models"], r["patterns"],
               r["new_backdrops"]))


def register(dp):
    from aiogram import F
    dp.message.register(cmd_rotate, Command("rotate"))
    dp.message.register(cmd_reserve, Command("reserve"))
    dp.callback_query.register(skip, F.data.startswith("rotskip:"))
    dp.callback_query.register(pick, F.data.startswith("rot:"))
    dp.startup.register(_startup)
