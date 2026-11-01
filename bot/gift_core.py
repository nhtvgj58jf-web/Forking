"""
gift_core.py - работает ВНУТРИ временного контейнера local/giftpy.
Команды (последняя строка вывода: JSON:{...}):
  soldout                      -> распроданные лимитные подарки + свободный резерв
  prepare <slug> <Title>       -> скачать выборку с Fragment, обрезать, проверить на заглушки
  install <slug> <sold_id> <stars> <upgrade> <total>
                               -> записать в базу и поставить в магазин
"""
import os, sys, io, gzip, json, re, time, random, shutil, hashlib, contextlib
from collections import Counter
from bson import Int64
from pymongo import MongoClient
from minio import Minio

sys.path.insert(0, "/app")
MIME, DDATE = "application/x-tgsticker", 1712160000
BAD_NAME = re.compile(r"^(model|pattern|tone)\s*[#0-9]", re.I)
MIN_MODEL, MIN_PATTERN = 800, 400          # байт gzip; меньше = заглушка
MAX_NEW_BACKDROPS = 40
REMOVE_SOLD = os.environ.get("REMOVE_SOLD") == "1"

db = MongoClient("mongodb://mongodb:27017")["tg"]
docs, cfg, sg, st = (db["eventflow-documentreadmodel"], db["star-gift-upgrade-config"],
                     db["star-gifts"], db["gift_rotate_state"])


def out(o):
    print("JSON:" + json.dumps(o, ensure_ascii=False), flush=True)


def reserve():
    try:
        return json.load(open("/reserve.json", encoding="utf-8"))
    except Exception:
        return []


def is_sold(g):
    if g.get("SoldOut"):
        return True
    r = g.get("AvailabilityRemains")
    return r is not None and int(r) <= 0


# ---------- soldout ----------
def cmd_soldout():
    done = {d["_id"] for d in st.find({"_id": {"$regex": "^sold-"}, "handled": True})}
    used = {d["slug"] for d in st.find({"_id": {"$regex": "^slug-"}})}
    sold = [{"GiftId": int(g["GiftId"]), "Title": g.get("Title")}
            for g in sg.find({"Limited": True}) if is_sold(g) and "sold-%d" % int(g["GiftId"]) not in done]
    cands = [r for r in reserve() if r.get("slug") not in used]
    out({"sold": sold, "cands": cands})


# ---------- trim / validate ----------
def trim(j):
    ends = [int(l["op"]) for l in j.get("layers", []) if l.get("op") is not None]
    top = j.get("op")
    if not ends or top is None:
        return False
    if top - max(ends) >= 15:                      # правило tgs_fix
        j["op"] = int(max(ends)); return True
    end, cnt = Counter(ends).most_common(1)[0]     # правило tgs_fix2
    if cnt >= 0.7 * len(ends) and top - end >= 15 and end >= 60:
        j["op"] = end; return True
    return False


def gz(j):
    return gzip.compress(json.dumps(j, separators=(",", ":")).encode(), 9)


def validate(S, ok_count):
    problems, warns, seen, trimmed = [], [], set(), [0]

    def chk(kind, x, minsize):
        who = "%s «%s»" % (kind, x.get("name"))
        if not x.get("name") or BAD_NAME.match(x["name"]):
            problems.append(who + ": нет настоящего имени")
        try:
            raw = open(x["file"], "rb").read()
            j = json.loads(gzip.decompress(raw))
        except Exception:
            problems.append(who + ": файл битый"); return
        if len(raw) < minsize:
            problems.append("%s: слишком маленький (%d Б), заглушка?" % (who, len(raw))); return
        if not j.get("layers") or not j.get("w") or not j.get("h") or (j.get("fr") or 0) <= 0 or (j.get("op") or 0) <= 0:
            problems.append(who + ": пустая или неполная анимация"); return
        h = hashlib.md5(json.dumps(j, sort_keys=True).encode()).hexdigest()
        if (kind, h) in seen:
            problems.append(who + ": копия другого варианта (заглушка?)")
        seen.add((kind, h))
        if kind == "модель":
            if trim(j):
                open(x["file"], "wb").write(gz(j)); trimmed[0] += 1
            sec = j["op"] / j["fr"]
            if sec > 8:
                warns.append("%s: длинная, %.1f с" % (who, sec))

    for x in S["models"]:
        chk("модель", x, MIN_MODEL)
    for x in S["patterns"]:
        chk("узор", x, MIN_PATTERN)
    hexre = re.compile(r"^[0-9A-Fa-f]{6}$")
    for b in S["backdrops"]:
        if not b.get("name") or BAD_NAME.match(b["name"]):
            problems.append("фон: нет настоящего имени")
        if not all(hexre.match(str(b.get(k, ""))) for k in ("center", "edge", "pattern", "text")):
            problems.append("фон «%s»: кривые цвета" % b.get("name"))
    for key, lab in (("models", "моделей"), ("patterns", "узоров"), ("backdrops", "фонов")):
        if len(S[key]) == 0:
            problems.append("нет ни одного из %s" % lab)
    if ok_count < 30:
        problems.append("мало экземпляров с Fragment: %d (нужно 30+)" % ok_count)
    warns.append("выборка %d экз.: очень редкие варианты могут отсутствовать" % ok_count)
    return problems, warns, trimmed[0]


def fx(url):
    import urllib.request, urllib.error
    for _ in range(3):
        try:
            return urllib.request.urlopen(urllib.request.Request(url, headers={"User-Agent": "Mozilla/5.0"}),
                                          timeout=30).read()
        except urllib.error.HTTPError as e:
            if e.code == 404:
                return None
            time.sleep(2)
        except Exception:
            time.sleep(2)
    return None


def cmd_prepare(slug, title=None):
    import fetch
    N = int(os.environ.get("SAMPLE", "150"))
    base = "/out/" + slug
    shutil.rmtree(base, ignore_errors=True)
    os.makedirs(base + "/models"); os.makedirs(base + "/patterns")
    M, P, B, ok, miss, noname = {}, {}, {}, 0, 0, 0
    for num in range(1, N + 1):
        data = fx(fetch.TGS % (slug, num))
        if data is None:
            miss += 1
            if miss >= 20:
                break
            continue
        miss = 0
        meta = fx("https://nft.fragment.com/gift/%s-%d.json" % (slug, num))
        try:
            a = {x["trait_type"]: x["value"] for x in json.loads(meta)["attributes"]}
            mn, pn, bn = a["Model"], a["Symbol"], a["Backdrop"]
            model, sym, c, e, pc = fetch.split(json.loads(gzip.decompress(data)))
        except Exception:
            noname += 1
            continue
        ok += 1
        M.setdefault(mn, {"cnt": 0, "obj": model})["cnt"] += 1
        P.setdefault(pn, {"cnt": 0, "obj": sym})["cnt"] += 1
        B.setdefault(bn, {"cnt": 0, "c": c, "e": e, "pc": pc})["cnt"] += 1
        time.sleep(0.3)
    S = {"title": title or slug, "models": [], "patterns": [], "backdrops": []}
    rar = lambda n: round(n * 1000 / max(ok, 1), 2)
    for i, (name, v) in enumerate(sorted(M.items(), key=lambda kv: -kv[1]["cnt"])):
        fn = "%s/models/%03d_%s.tgs" % (base, i, fetch.norm(name)); fetch.save(fn, v["obj"])
        S["models"].append({"name": name, "rarity": rar(v["cnt"]), "file": fn})
    for i, (name, v) in enumerate(sorted(P.items(), key=lambda kv: -kv[1]["cnt"])):
        fn = "%s/patterns/%03d_%s.tgs" % (base, i, fetch.norm(name)); fetch.save(fn, v["obj"])
        S["patterns"].append({"name": name, "rarity": rar(v["cnt"]), "file": fn})
    for name, v in sorted(B.items(), key=lambda kv: -kv[1]["cnt"]):
        c = v["c"]; lum = int(c[0:2], 16) * .3 + int(c[2:4], 16) * .59 + int(c[4:6], 16) * .11
        S["backdrops"].append({"name": name, "center": c, "edge": v["e"], "pattern": v["pc"],
                               "text": "FFFFFF" if lum < 150 else "2B2B2B", "rarity": rar(v["cnt"])})
    problems, warns, trimmed = validate(S, ok)
    if noname:
        warns.append("пропущено экземпляров без имени или с битым файлом: %d" % noname)
    if sg.find_one({"Title": S["title"]}):
        problems.append("«%s» уже есть в магазине" % S["title"])
    S["checked"] = not problems
    json.dump(S, open(base + "/summary.json", "w", encoding="utf-8"), ensure_ascii=False, indent=1)
    out({"ok": not problems, "problems": problems, "warns": warns, "trimmed": trimmed, "title": S["title"],
         "models": len(S["models"]), "patterns": len(S["patterns"]), "backdrops": len(S["backdrops"])})


# ---------- install ----------
def n1000(vals):
    t = sum(vals) or 1
    r = [max(1, round(v * 1000 / t)) for v in vals]
    r[r.index(max(r))] += 1000 - sum(r)
    return r


def minio():
    ep = os.environ["Minio__Endpoint"].replace("http://", "").replace("https://", "")
    return Minio(ep, access_key=os.environ["Minio__AccessKey"],
                 secret_key=os.environ["Minio__SecretKey"], secure=False)


def mkdoc(mc, bucket, path, slug):
    name = "nft_%s_%s" % (slug, os.path.basename(path))
    data = open(path, "rb").read()
    ex = docs.find_one({"Name": name})
    if ex:
        did = int(ex["DocumentId"])
        mc.put_object(bucket, str(did), io.BytesIO(data), length=len(data), content_type=MIME)
        docs.update_one({"_id": ex["_id"]}, {"$set": {"Size": len(data)}})
        return did, int(ex["AccessHash"]), len(data)
    while True:
        did = random.randint(10**12, 10**15)
        if not docs.find_one({"DocumentId": Int64(did)}):
            break
    ah = random.randint(-2**62, 2**62)
    mc.put_object(bucket, str(did), io.BytesIO(data), length=len(data), content_type=MIME)
    docs.insert_one({"_id": "documentreadmodel-%d" % did, "Id": "documentreadmodel-%d" % did,
                     "DocumentId": Int64(did), "AccessHash": Int64(ah), "Date": int(time.time()), "DcId": 1,
                     "MimeType": MIME, "Size": len(data), "Name": name, "Thumbs": None, "VideoThumbs": None,
                     "Attributes": None, "Attributes2": [], "CreatorId": None, "Fingerprint": None,
                     "Md5CheckSum": None, "ThumbId": None, "VideoThumbId": None, "Version": 1})
    return did, ah, len(data)


def ent(**kw):
    d = {"FileReference": [], "DocumentDate": DDATE, "MimeType": MIME, "DcId": 1}
    d.update(kw)
    return d


def cmd_install(slug, sold_id, stars, upgrade, total):
    sold_id, stars, upgrade, total = int(sold_id), int(stars), int(upgrade), int(total)
    try:
        S = json.load(open("/out/%s/summary.json" % slug, encoding="utf-8"))
    except Exception:
        out({"ok": False, "error": "нет summary.json, сначала prepare"}); return
    if not S.get("checked"):
        out({"ok": False, "error": "проверка не пройдена"}); return
    if sg.find_one({"Title": S["title"]}):
        out({"ok": False, "error": "уже есть в магазине"}); return
    mc, bucket = minio(), os.environ.get("Minio__BucketName", "tg-files")
    list(cfg.aggregate([{"$out": "star-gift-upgrade-config_bak_rot"}]))
    list(sg.aggregate([{"$out": "star-gifts_bak_rot"}]))
    gid = max(int(g["GiftId"]) for g in sg.find({}, {"GiftId": 1})) + 1
    rows = []
    for t, key in (("model", "models"), ("pattern", "patterns")):
        items = S[key]
        for x, r in zip(items, n1000([x["rarity"] for x in items])):
            did, ah, sz = mkdoc(mc, bucket, x["file"], slug)
            x["_doc"] = (did, ah, sz)
            rows.append(ent(Type=t, Name=x["name"], GiftId=Int64(gid), RarityPermille=r,
                            DocumentId=Int64(did), DocumentAccessHash=Int64(ah), DocumentSize=Int64(sz)))
    cfg.delete_many({"GiftId": Int64(gid), "Type": {"$in": ["model", "pattern"]}})
    cfg.insert_many(rows)

    # фоны: общий набор. Новые цвета добавляем, у старых "Tone ..." ставим настоящее имя.
    old = list(cfg.find({"Type": "backdrop"}))
    have = {(format(o["CenterColor"], "06x"), format(o["EdgeColor"], "06x")): o for o in old}
    new = [b for b in S["backdrops"] if (b["center"].lower(), b["edge"].lower()) not in have]
    new = sorted(new, key=lambda b: -b["rarity"])[:MAX_NEW_BACKDROPS]
    for b in S["backdrops"]:
        o = have.get((b["center"].lower(), b["edge"].lower()))
        if o and (o.get("Name") or "").startswith("Tone "):
            cfg.update_one({"_id": o["_id"]}, {"$set": {"Name": b["name"]}})
    if new:
        weights = [float(o["RarityPermille"]) for o in old] + [max(0.5, b["rarity"] / 8) for b in new]
        rr = n1000(weights)
        for o, r in zip(old, rr):
            cfg.update_one({"_id": o["_id"]}, {"$set": {"RarityPermille": r}})
        nid = max([o.get("BackdropId", 0) for o in old] + [0]) + 1
        cfg.insert_many([{"Type": "backdrop", "Name": b["name"], "GiftId": Int64(0),
                          "RarityPermille": rr[len(old) + i], "BackdropId": nid + i,
                          "CenterColor": int(b["center"], 16), "EdgeColor": int(b["edge"], 16),
                          "PatternColor": int(b["pattern"], 16), "TextColor": int(b["text"], 16)}
                         for i, b in enumerate(new)])

    top = max(S["models"], key=lambda x: x["rarity"])["_doc"]
    sg.insert_one({"GiftId": Int64(gid), "Stars": Int64(stars), "ConvertStars": Int64(int(stars * 0.8)),
                   "UpgradeStars": Int64(upgrade), "Limited": True, "SoldOut": False, "Birthday": False,
                   "RequirePremium": False, "LimitedPerUser": False,
                   "AvailabilityTotal": total, "AvailabilityRemains": total, "Title": S["title"],
                   "DocumentId": Int64(top[0]), "DocumentAccessHash": Int64(top[1]), "FileReference": [],
                   "DocumentDate": DDATE, "MimeType": MIME, "DocumentSize": Int64(top[2]), "DcId": 1,
                   "IsAuction": False, "RandomId": Int64(gid)})
    if REMOVE_SOLD:
        sg.delete_one({"GiftId": Int64(sold_id)})
    st.replace_one({"_id": "sold-%d" % sold_id}, {"_id": "sold-%d" % sold_id, "handled": True,
                                                  "new": gid, "slug": slug}, upsert=True)
    st.replace_one({"_id": "slug-" + slug}, {"_id": "slug-" + slug, "slug": slug, "gift": gid}, upsert=True)
    out({"ok": True, "GiftId": gid, "title": S["title"], "stars": stars, "upgrade": upgrade, "total": total,
         "models": len(S["models"]), "patterns": len(S["patterns"]), "new_backdrops": len(new)})


if __name__ == "__main__":
    c, a = sys.argv[1], sys.argv[2:]
    {"soldout": cmd_soldout, "prepare": cmd_prepare, "install": cmd_install}[c](*a)
