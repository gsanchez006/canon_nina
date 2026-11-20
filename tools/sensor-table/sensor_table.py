"""Maintain CanonSensors.json, the plugin's table of Canon EOS sensor geometry.

Subcommands (run with --help for details):
  museum  ID|URL           Read names, sensor width and effective width from a Canon Camera Museum product page.
  measure FILE...          Decode CR2/CR3 files exactly like NINA does and print the image size NINA gets.
  pixls   FOLDER           Pick the full-resolution sample of a model on raw.pixls.us, download it and measure it.
  add     ...              Add a camera to CanonSensors.json, or update the entry that has the same name.
  recheck [--only NAME]    Decode the recorded samples again and report sizes that changed (after a NINA update).

The decoded size must come from NINA's own decoder (FreeImage.dll in the NINA install, which contains LibRaw
0.20): NINA crops every Canon RAW to the "Raw.Frame" rectangle that decoder reports, and for many newer bodies that is
not what current LibRaw, Canon's specs or exiftool say. Needs Windows, 64-bit Python 3.10+ and NINA installed;
`museum` also needs `pip install pypdf cryptography` for spec sheets that are PDFs.
"""
import argparse
import ctypes
import html
import io
import json
import os
import re
import struct
import subprocess
import sys
import tempfile
import time
import urllib.parse
import urllib.request
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
TABLE = REPO / "CanonSensors.json"
DEFAULT_NINA = Path(os.environ.get("ProgramFiles", r"C:\Program Files")) / "N.I.N.A. - Nighttime Imaging 'N' Astronomy"
MUSEUM = "https://global.canon"
PIXLS = "https://raw.pixls.us"
USER_AGENT = {"User-Agent": "Mozilla/5.0 (canon-nina sensor-table tool)"}


# --------------------------------------------------------------------------------------------------------------------
# Names. Python twin of CanonSensorDatabase.NormalizeModelName in the plugin: keep the two identical.
# --------------------------------------------------------------------------------------------------------------------

def normalize(name):
    if not name or not name.strip():
        return ""
    s = name.upper().replace("-", " ").replace("_", " ")
    s = re.sub(r"\b(CANON|EOS|DIGITAL)\b", " ", s)
    s = re.sub(r"\s+", " ", s).strip()
    roman = {"I": "1", "II": "2", "III": "3", "IV": "4", "V": "5"}

    def mark(m):
        token = m.group(1) or m.group(2) or m.group(3)
        return "M" + roman.get(token, token)

    s = re.sub(r"\b(?:MARK|MK\.?)\s*(IV|III|II|I|V|\d)\b|(?<=[0-9A-Z])\s+(III|II|IV)\b|(?<=[0-9])M(\d)\b", mark, s)
    return "".join(c for c in s if c.isalnum())


def display_name(name):
    """'Canon EOS R6m3' -> 'EOS R6m3' (the table lists names without the brand)."""
    return re.sub(r"^\s*Canon\s+", "", name.strip())


# --------------------------------------------------------------------------------------------------------------------
# HTTP
# --------------------------------------------------------------------------------------------------------------------

def http_get(url, byte_range=None):
    headers = dict(USER_AGENT)
    if byte_range:
        headers["Range"] = f"bytes={byte_range[0]}-{byte_range[1]}"
    for attempt in range(3):
        try:
            with urllib.request.urlopen(urllib.request.Request(url, headers=headers), timeout=300) as response:
                return response.read()
        except Exception:
            if attempt == 2:
                raise
            time.sleep(2)


def http_size(url):
    request = urllib.request.Request(url, headers=USER_AGENT, method="HEAD")
    with urllib.request.urlopen(request, timeout=60) as response:
        return int(response.headers["Content-Length"])


# --------------------------------------------------------------------------------------------------------------------
# NINA's decoder and exiftool
# --------------------------------------------------------------------------------------------------------------------

def nina_paths(nina_dir):
    nina_dir = Path(nina_dir)
    freeimage = nina_dir / "External" / "x64" / "FreeImage" / "FreeImage.dll"
    exiftool = nina_dir / "Utility" / "ExifTool" / "exiftool.exe"
    for p in (freeimage, exiftool):
        if not p.exists():
            sys.exit(f"Not found: {p}\nPass the NINA install folder with --nina.")
    return freeimage, exiftool


def decode_in_this_process(freeimage, path):
    """FreeImage_Load(FIF_RAW, path, RAW_UNPROCESSED) and the Raw.Frame comments, as NINA's FreeImageConverter does."""
    fi = ctypes.WinDLL(str(freeimage))
    fi.FreeImage_Load.restype = ctypes.c_void_p
    fi.FreeImage_Load.argtypes = [ctypes.c_int, ctypes.c_char_p, ctypes.c_int]
    fi.FreeImage_Unload.argtypes = [ctypes.c_void_p]
    fi.FreeImage_GetMetadata.restype = ctypes.c_int
    fi.FreeImage_GetMetadata.argtypes = [ctypes.c_int, ctypes.c_void_p, ctypes.c_char_p, ctypes.POINTER(ctypes.c_void_p)]
    fi.FreeImage_GetTagValue.restype = ctypes.c_void_p
    fi.FreeImage_GetTagValue.argtypes = [ctypes.c_void_p]
    fif_raw, raw_unprocessed, fimd_comments = 34, 8, 0

    dib = fi.FreeImage_Load(fif_raw, str(path).encode("mbcs"), raw_unprocessed)
    if not dib:
        return {"error": "NINA's decoder could not open the file"}
    try:
        out = {}
        for key in ("Left", "Top", "Width", "Height"):
            tag = ctypes.c_void_p()
            if fi.FreeImage_GetMetadata(fimd_comments, dib, f"Raw.Frame.{key}".encode(), ctypes.byref(tag)):
                out[key.lower()] = int(ctypes.c_char_p(fi.FreeImage_GetTagValue(tag)).value)
        return out
    finally:
        fi.FreeImage_Unload(dib)


def decode(freeimage, path, timeout=180):
    """Runs the decode in a child process: FreeImage can hang or crash on unusual files."""
    try:
        p = subprocess.run([sys.executable, "-I", __file__, "_decode", str(freeimage), str(path)],
                           capture_output=True, text=True, timeout=timeout)
        lines = p.stdout.strip().splitlines()
        return json.loads(lines[-1]) if lines else {"error": f"decoder crashed: {p.stderr.strip()[-300:]}"}
    except subprocess.TimeoutExpired:
        return {"error": "decoder timed out"}


EXIF_TAGS = ["Model", "SensorWidth", "SensorHeight", "ImageWidth", "ImageHeight", "Quality",
             "FocalPlaneXResolution", "FocalPlaneResolutionUnit"]
CANON_QUALITY_CRAW = 7  # Canon maker-note Quality: 4 = RAW, 7 = C-RAW (compressed CR3)


def exif(exiftool, path):
    p = subprocess.run([str(exiftool), "-config", "", "-j", "-n"] + [f"-{t}" for t in EXIF_TAGS] + [str(path)],
                       capture_output=True, text=True, timeout=120)
    try:
        data = json.loads(p.stdout)[0]
    except Exception:
        return {}
    data.pop("SourceFile", None)
    return data


def measure_file(freeimage, exiftool, path):
    info = exif(exiftool, path)
    result = {"file": str(path), "model": info.get("Model"), "rawWidth": info.get("SensorWidth"),
              "rawHeight": info.get("SensorHeight"), "jpegWidth": info.get("ImageWidth"),
              "jpegHeight": info.get("ImageHeight")}
    result.update(decode(freeimage, path))
    return result


# --------------------------------------------------------------------------------------------------------------------
# Canon Camera Museum
# --------------------------------------------------------------------------------------------------------------------

def text_of(fragment):
    return " ".join(html.unescape(re.sub(r"<[^>]+>", " ", fragment)).split())


def parse_spec(text):
    """Sensor size: the first "W x H mm" pair with sensor-like proportions (APS-C to full frame). Effective width:
    the 3:2 image size the sheet repeats most (Large JPEG, Large/Fine, Large/Normal and RAW all list it; one-off
    sizes are total-pixel counts or crops), largest first on a tie."""
    t = text.replace("\u00d7", "x")
    sensor = None
    for a, b in re.findall(r"(\d{2}(?:\.\d{1,2})?)\s*(?:mm)?\s*x\s*(\d{2}(?:\.\d{1,2})?)\s*mm", t, re.I):
        w, h = float(a), float(b)
        if 17 <= w <= 45 and 1.4 <= w / h <= 1.6:
            sensor = (w, h)
            break
    counts = {}
    for a, b in re.findall(r"\b(\d{4})\s*x\s*(\d{4})\b", t):
        counts[(int(a), int(b))] = counts.get((int(a), int(b)), 0) + 1
    three_two = sorted(((n, w) for (w, h), n in counts.items() if abs(w / h - 1.5) < 0.02), reverse=True)
    return {"sensorWidthMm": sensor[0] if sensor else None,
            "sensorHeightMm": sensor[1] if sensor else None,
            "effectiveWidth": three_two[0][1] if three_two else None,
            "imageSizes": sorted(counts, key=lambda p: -p[0] * p[1])[:6]}


def museum_lookup(product):
    url = product if product.startswith("http") else f"{MUSEUM}/en/c-museum/product/{product}.html"
    page = http_get(url).decode("utf-8", "replace")
    names = []
    for raw in re.findall(r'<p class="title_i">(.*?)</p>', page, re.S):
        for part in re.split(r"\s*[,/]\s*", text_of(raw)):
            if part and part not in names:
                names.append(part)
    year = re.search(r"Marketed\s*</[^>]+>\s*<[^>]+>\s*\w+\s+(\d{4})", page) or re.search(r"Marketed\D{0,80}?(\d{4})", text_of(page))
    spec = parse_spec(text_of(page))
    source = f"Canon Camera Museum {Path(urllib.parse.urlparse(url).path).stem}"
    pdf = re.search(r'href="([^"]+\.pdf)"', page)
    if pdf and (spec["sensorWidthMm"] is None or spec["effectiveWidth"] is None):
        try:
            from pypdf import PdfReader
        except ImportError:
            sys.exit("This spec sheet is a PDF: pip install pypdf cryptography")
        link = pdf.group(1)
        data = http_get(MUSEUM + link if link.startswith("/") else link)
        spec = parse_spec(" ".join((p.extract_text() or "") for p in PdfReader(io.BytesIO(data)).pages))
        source += " (spec PDF)"
    return {"names": names, "year": int(year.group(1)) if year else None, "specSource": source, **spec}


# --------------------------------------------------------------------------------------------------------------------
# raw.pixls.us
# --------------------------------------------------------------------------------------------------------------------

def pixls_files(folder):
    listing = http_get(f"{PIXLS}/data/Canon/{urllib.parse.quote(folder)}/").decode("utf-8", "replace")
    names = [urllib.parse.unquote(h) for h in re.findall(r'href="([^"?/][^"]*)"', listing)]
    return [n for n in names if n.lower().endswith((".cr2", ".cr3"))]


def pixls_url(folder, name):
    return f"{PIXLS}/download/data/Canon/{urllib.parse.quote(folder)}/{urllib.parse.quote(name.lstrip('./'))}"


def fetch_header_copy(url, size, target):
    """CR2: the first 2 MB plus the start of the raw data, at the original offsets, in a zero-filled file of the
    original size. NINA's decoder reads the size from those parts. CR3 cannot be decoded that way: fetch it whole."""
    head = http_get(url, (0, min(size, 2 * 1024 * 1024) - 1))
    with open(target, "wb") as f:
        f.write(head)
        f.truncate(size)
    if head[8:10] != b"CR":
        return
    endian = "<" if head[:2] == b"II" else ">"
    raw_ifd = struct.unpack(endian + "I", head[12:16])[0]
    if raw_ifd + 512 > len(head):
        ifd = http_get(url, (raw_ifd, raw_ifd + 4095))
        with open(target, "r+b") as f:
            f.seek(raw_ifd)
            f.write(ifd)
        head = head.ljust(raw_ifd, b"\0")[:raw_ifd] + ifd
    count = struct.unpack(endian + "H", head[raw_ifd:raw_ifd + 2])[0]
    for i in range(count):
        tag, _, _, value = struct.unpack(endian + "HHII", head[raw_ifd + 2 + 12 * i: raw_ifd + 14 + 12 * i])
        if tag == 0x0111 and value >= len(head):
            chunk = http_get(url, (value, min(size, value + 256 * 1024) - 1))
            with open(target, "r+b") as f:
                f.seek(value)
                f.write(chunk)


def pixls_measure(freeimage, exiftool, folder, workdir, max_candidates=8):
    files = pixls_files(folder)
    if not files:
        sys.exit(f"No CR2/CR3 files in raw.pixls.us folder '{folder}' (folder names are listed at {PIXLS}/data/Canon/)")
    # Crop-mode, sRAW/mRAW, C-RAW and dual-pixel files last: the full-resolution uncompressed RAW is what NINA can
    # decode. NINA's decoder (LibRaw 0.20) cannot open the C-RAW files of many R bodies.
    files.sort(key=lambda f: (bool(re.search(r"(s|m|c)raw|crop|(?<!no)dual|1\.6|aps", f, re.I)), f))
    candidates = []
    for name in files[:max_candidates]:
        url = pixls_url(folder, name)
        size = http_size(url)
        header = Path(workdir) / ("hdr_" + re.sub(r"[^A-Za-z0-9._-]", "_", name.lstrip("./")))
        header.write_bytes(http_get(url, (0, min(size, 2 * 1024 * 1024) - 1)))
        info = exif(exiftool, header)
        area = (info.get("SensorWidth") or 0) * (info.get("SensorHeight") or 0)
        plain = info.get("Quality") != CANON_QUALITY_CRAW and not re.search(r"(?<!no)dual", name, re.I)
        candidates.append((area, plain, -size, name, url, size, info.get("Model")))
        print(f"  {name}: {info.get('Model')} raw {info.get('SensorWidth')}x{info.get('SensorHeight')}, "
              f"{'C-RAW' if info.get('Quality') == CANON_QUALITY_CRAW else 'RAW'}, {size / 1e6:.1f} MB", file=sys.stderr)
    candidates.sort(reverse=True)
    _, plain, _, name, url, size, model = candidates[0]
    if not plain:
        print("  warning: no uncompressed full-size RAW among the samples; NINA's decoder may not open this one",
              file=sys.stderr)
    local = Path(workdir) / re.sub(r"[^A-Za-z0-9._-]", "_", name.lstrip("./"))
    if name.lower().endswith(".cr2"):
        fetch_header_copy(url, size, local)
    else:
        print(f"  downloading {name} ({size / 1e6:.1f} MB)...", file=sys.stderr)
        local.write_bytes(http_get(url))
    result = measure_file(freeimage, exiftool, local)
    result["sizeSource"] = f"NINA decoder on raw.pixls.us Canon/{folder}/{name.lstrip('./')}"
    return result


# --------------------------------------------------------------------------------------------------------------------
# CanonSensors.json
# --------------------------------------------------------------------------------------------------------------------

def load_table():
    return json.loads(TABLE.read_text(encoding="utf-8"))


def save_table(doc):
    doc["cameras"].sort(key=lambda c: (c.get("year") or 9999, c["model"]))
    TABLE.write_text(json.dumps(doc, indent=2, ensure_ascii=False) + "\n", encoding="utf-8", newline="\n")


def find_entry(cameras, names):
    keys = {normalize(n) for n in names}
    hits = [c for c in cameras if keys & {normalize(n) for n in [c["model"]] + c.get("aliases", [])}]
    if len(hits) > 1:
        sys.exit("These names match more than one camera already in the table: "
                 + ", ".join(c["model"] for c in hits))
    return hits[0] if hits else None


# --------------------------------------------------------------------------------------------------------------------
# Commands
# --------------------------------------------------------------------------------------------------------------------

def cmd_museum(args):
    print(json.dumps(museum_lookup(args.product), indent=2))


def cmd_measure(args):
    freeimage, exiftool = nina_paths(args.nina)
    for f in args.files:
        print(json.dumps(measure_file(freeimage, exiftool, Path(f).resolve())))


def cmd_pixls(args):
    freeimage, exiftool = nina_paths(args.nina)
    with tempfile.TemporaryDirectory() as workdir:
        print(json.dumps(pixls_measure(freeimage, exiftool, args.folder, workdir), indent=2))


def cmd_add(args):
    names, year, spec_source = [], args.year, args.spec_source
    sensor_mm, effective = args.sensor_width_mm, args.effective_width

    if args.museum:
        m = museum_lookup(args.museum)
        print(f"Museum: {m['names']}, {m['year']}, sensor width {m['sensorWidthMm']} mm, effective width "
              f"{m['effectiveWidth']} px (image sizes on the page: {m['imageSizes']})", file=sys.stderr)
        names += m["names"]
        year = year or m["year"]
        sensor_mm = sensor_mm or m["sensorWidthMm"]
        effective = effective or m["effectiveWidth"]
        spec_source = spec_source or m["specSource"]

    measured = None
    if args.raw or args.pixls:
        freeimage, exiftool = nina_paths(args.nina)
        if args.raw:
            measured = measure_file(freeimage, exiftool, Path(args.raw).resolve())
            measured["sizeSource"] = f"NINA decoder on {Path(args.raw).name}" + (f" ({args.raw_note})" if args.raw_note else "")
        else:
            with tempfile.TemporaryDirectory() as workdir:
                measured = pixls_measure(freeimage, exiftool, args.pixls, workdir)
        print(f"Measured: {json.dumps(measured)}", file=sys.stderr)
        if measured.get("model"):
            names.append(display_name(measured["model"]))

    if args.model:
        names.insert(0, args.model)
    names += args.alias or []
    names = [display_name(n) for n in names]
    if not names:
        sys.exit("No camera name: pass --model, --museum, --raw or --pixls")

    # Updating a camera that is already in the table: keep the spec values that were not given again.
    doc = load_table()
    entry = find_entry(doc["cameras"], names)
    if entry is not None:
        year = year or entry.get("year")
        if not sensor_mm and not effective:
            sensor_mm, effective = entry.get("sensorWidthMm"), entry.get("effectiveWidth")
            spec_source = spec_source or entry.get("specSource")

    jpeg_w, jpeg_h = (measured or {}).get("jpegWidth"), (measured or {}).get("jpegHeight")
    if not effective and jpeg_w and jpeg_h and abs(jpeg_w / jpeg_h - 1.5) < 0.02 and jpeg_w >= 3000:
        # CR3 bodies record the full-size JPEG dimensions; old CR2 bodies record the preview's, hence the >= 3000.
        effective = jpeg_w
        spec_source = (spec_source or "") + " (effective width from the RAW's EXIF image size)"
        print(f"Effective width {effective} px taken from the RAW's EXIF", file=sys.stderr)

    if not sensor_mm or not effective:
        sys.exit("Need the sensor width (--sensor-width-mm) and effective image width (--effective-width), or --museum")
    if not spec_source:
        sys.exit("Say where the sensor width and effective width come from: --spec-source")

    width = height = 0
    size_source = "unknown; taken from the first frame"
    if measured and measured.get("width") and measured.get("height"):
        width, height, size_source = measured["width"], measured["height"], measured["sizeSource"]
    elif measured:
        size_source = f"{measured.get('error', 'decode failed')} ({measured.get('sizeSource', '')}); taken from the first frame"
    if args.decoded:
        width, height = (int(v) for v in args.decoded.lower().split("x"))
        size_source = args.size_source or "NINA log of the camera's first frame"

    if entry is None:
        entry = {"model": names[0], "aliases": []}
        doc["cameras"].append(entry)
        print(f"Adding {names[0]}", file=sys.stderr)
    else:
        print(f"Updating {entry['model']}", file=sys.stderr)
    known = {normalize(n) for n in [entry["model"]] + entry.get("aliases", [])}
    for n in names:
        if normalize(n) and normalize(n) not in known:
            entry.setdefault("aliases", []).append(n)
            known.add(normalize(n))
    # Every name must still point at exactly one camera.
    for other in doc["cameras"]:
        if other is not entry and known & {normalize(n) for n in [other["model"]] + other.get("aliases", [])}:
            sys.exit(f"Name clash with {other['model']}: nothing written")

    entry.update({"year": year or entry.get("year"), "sensorWidthMm": float(sensor_mm), "effectiveWidth": int(effective),
                  "specSource": spec_source})
    if width and height or "decodedWidth" not in entry:
        entry.update({"decodedWidth": width, "decodedHeight": height, "sizeSource": size_source})
    save_table(doc)
    pitch = float(sensor_mm) * 1000 / int(effective)
    print(json.dumps(entry, indent=2, ensure_ascii=False))
    print(f"Pixel size {pitch:.2f} um. Now run: dotnet test Canon_RAW.sln", file=sys.stderr)


def cmd_recheck(args):
    """Decode every table entry's raw.pixls.us sample again and report sizes that changed (after a NINA update)."""
    freeimage, exiftool = nina_paths(args.nina)
    changed = 0
    with tempfile.TemporaryDirectory() as workdir:
        for cam in load_table()["cameras"]:
            if args.only and normalize(args.only) not in {normalize(n) for n in [cam["model"]] + cam.get("aliases", [])}:
                continue
            m = re.search(r"raw\.pixls\.us Canon/(.+?)/([^/()]+?)(?: \(|$)", cam.get("sizeSource", ""))
            if not m:
                print(f"{cam['model']}: no raw.pixls.us sample recorded, skipped", file=sys.stderr)
                continue
            folder, name = m.group(1), m.group(2).strip()
            url = pixls_url(folder, name)
            local = Path(workdir) / re.sub(r"[^A-Za-z0-9._-]", "_", name)
            size = http_size(url)
            if name.lower().endswith(".cr2"):
                fetch_header_copy(url, size, local)
            else:
                local.write_bytes(http_get(url))
            result = decode(freeimage, local)
            local.unlink(missing_ok=True)
            now = (result.get("width", 0), result.get("height", 0))
            was = (cam.get("decodedWidth", 0), cam.get("decodedHeight", 0))
            status = "same" if now == was else "CHANGED"
            changed += now != was
            print(f"{cam['model']}: table {was[0]}x{was[1]}, NINA now {now[0]}x{now[1]} {status} {result.get('error', '')}")
    print(f"{changed} changed", file=sys.stderr)


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--nina", default=str(DEFAULT_NINA), help="NINA install folder (default: %(default)s)")
    sub = parser.add_subparsers(dest="command", required=True)

    p = sub.add_parser("museum", help="names and spec from a Canon Camera Museum product page")
    p.add_argument("product", help="product id such as dslr908, or the page URL")
    p.set_defaults(func=cmd_museum)

    p = sub.add_parser("measure", help="decode CR2/CR3 files like NINA does")
    p.add_argument("files", nargs="+")
    p.set_defaults(func=cmd_measure)

    p = sub.add_parser("pixls", help="measure the full-resolution raw.pixls.us sample of a model")
    p.add_argument("folder", help="folder name under https://raw.pixls.us/data/Canon/, e.g. 'EOS R8'")
    p.set_defaults(func=cmd_pixls)

    p = sub.add_parser("add", help="add or update a camera in CanonSensors.json")
    p.add_argument("--museum", help="Canon Camera Museum product id or URL: names, year, sensor width, effective width")
    p.add_argument("--raw", help="a full-resolution CR2/CR3 from the camera, to measure the decoded size")
    p.add_argument("--raw-note", help="where --raw came from, for the sizeSource field")
    p.add_argument("--pixls", help="raw.pixls.us folder to measure instead of --raw")
    p.add_argument("--decoded", help="decoded size WIDTHxHEIGHT, e.g. from the plugin's NINA log line")
    p.add_argument("--size-source", help="where --decoded came from")
    p.add_argument("--model", help="display name, e.g. 'EOS R6 Mark III' (default: the museum's first name)")
    p.add_argument("--alias", action="append", help="another name of the same camera (repeatable)")
    p.add_argument("--year", type=int)
    p.add_argument("--sensor-width-mm", type=float, help="Canon's published sensor width")
    p.add_argument("--effective-width", type=int, help="Canon's largest JPEG/effective image width in pixels")
    p.add_argument("--spec-source", help="where the sensor width / effective width come from")
    p.set_defaults(func=cmd_add)

    p = sub.add_parser("recheck", help="re-decode the recorded samples and report sizes that changed (after a NINA update)")
    p.add_argument("--only", help="check just this camera")
    p.set_defaults(func=cmd_recheck)

    if len(sys.argv) == 4 and sys.argv[1] == "_decode":
        print(json.dumps(decode_in_this_process(sys.argv[2], sys.argv[3])))
        return
    args = parser.parse_args()
    args.func(args)


if __name__ == "__main__":
    main()
