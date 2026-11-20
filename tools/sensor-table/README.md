# Canon sensor table

`CanonSensors.json` in the repository root is the plugin's table of Canon EOS cameras. It is embedded in the DLL, and
when a camera connects on NINA's Canon driver the plugin looks it up by the model name the camera reports. It then
sets NINA's camera pixel size and the framing assistant's pixel size, width and height.

This folder has the tool that measures and adds cameras: `sensor_table.py`.

## What each field means

```json
{
  "model": "EOS Rebel T8i",
  "aliases": ["EOS Kiss X10i", "EOS 850D"],
  "year": 2020,
  "sensorWidthMm": 22.3,
  "effectiveWidth": 6000,
  "decodedWidth": 6024,
  "decodedHeight": 4020,
  "specSource": "Canon Camera Museum dslr892",
  "sizeSource": "NINA 3.2 decoder on raw.pixls.us Canon/EOS 850D/IMG_1837.CR3"
}
```

| Field | Meaning |
|---|---|
| `model`, `aliases` | Every name the camera is sold under or reports itself as. Case, spaces, hyphens, the words Canon / EOS / Digital, and the spellings "Mark II" / "Mk II" / "m2" / "II" are all ignored when matching. Regional names matter: the same body is a Rebel in North America, a Kiss in Japan and an xxxD elsewhere. |
| `sensorWidthMm`, `effectiveWidth` | Canon's published sensor width and largest (effective / L JPEG) image width. Pixel size = `sensorWidthMm * 1000 / effectiveWidth`, rounded to 0.01 µm. |
| `decodedWidth`, `decodedHeight` | The size of the image **NINA** decodes from the camera's RAW file. This is *not* Canon's effective size, and not always the full sensor readout. NINA crops each RAW to the frame its own decoder reports (FreeImage with LibRaw 0.20, in `External\x64\FreeImage` of the NINA install), so the size must be measured with that decoder. `0` / `0` = unknown: the plugin takes the size from the camera's first frame. |
| `year`, `specSource`, `sizeSource` | Documentation only: where the numbers came from. |

## Requirements

- Windows, 64-bit Python 3.10 or later, and NINA installed. The tool uses NINA's own `FreeImage.dll` and `exiftool.exe`. If NINA is not in the default folder, pass `--nina "D:\Path\To\NINA"` **before** the subcommand.
- For the `museum` lookups of newer cameras, whose spec sheets are PDFs: `pip install pypdf cryptography`

## Adding a new camera

The [Canon Camera Museum](https://global.canon/en/c-museum/camera.html?s=dslr&s2=all) has a product page for each camera, for example `https://global.canon/en/c-museum/product/dslr908.html`. Its id (`dslr908`) gives the tool the camera's names in every region, its year, and the spec values. Pick whichever case below fits:

**1. You have the camera (or a RAW file from it).** Shoot one frame as **uncompressed RAW**, not C-RAW, because NINA's decoder cannot open the C-RAW files of many R bodies. Then:

```
python tools/sensor-table/sensor_table.py add --museum dslr916 --raw D:\frames\IMG_0001.CR3
```

**2. Someone else has published a sample.** [raw.pixls.us](https://raw.pixls.us/data/Canon/) collects sample files for nearly every camera. The tool picks the full-resolution uncompressed sample and decodes it. CR3 files are downloaded whole; CR2 files only partly.

```
python tools/sensor-table/sensor_table.py add --museum dslr916 --pixls "EOS R6 Mark III"
```

**3. The museum has no page yet.** Take the values from Canon's own spec page, for example `https://www.canon.co.uk/cameras/<model>/specifications/` ("Image sensor size" and the largest "Recorded pixels" / JPEG L size), and name every regional variant:

```
python tools/sensor-table/sensor_table.py add --model "EOS R6 Mark III" --year 2025 --sensor-width-mm 35.9 --effective-width 6960 --spec-source "canon.co.uk EOS R6 Mark III specifications" --pixls "Canon EOS R6 Mark III"
```

**4. Only a NINA log is available.** When an unknown Canon camera connects, the plugin measures the camera's first frame and logs a line like the one below. Pass that size with `--decoded`:

```
CanonAstroImage: to add the Canon EOS R9 to the built-in table, report this line: model "Canon EOS R9", decoded 6192x4060
python tools/sensor-table/sensor_table.py add --museum dslr920 --model "EOS R9" --decoded 6192x4060 --size-source "NINA log, first frame"
```

`add` updates the existing entry when any of the names already matches a camera in the table. It refuses to write when a name would match two cameras. Afterwards:

1. Review the change with `git diff CanonSensors.json`. Check that the museum's effective width is the Large JPEG / RAW size the tool printed (it lists every image size it found on the page) and that the camera's EXIF model name ended up in `aliases`.
2. Run `dotnet test Canon_RAW.sln`. The tests reject entries that share a name, pixel sizes outside 2–9 µm, and decoded sizes that don't fit an APS-C or full-frame sensor.
3. Bump the plugin version, add a CHANGELOG line ("Added EOS ... to the sensor table") and build.

## Other commands

```
python tools/sensor-table/sensor_table.py museum dslr908          # names, year, sensor width, effective width, image sizes
python tools/sensor-table/sensor_table.py measure IMG_0001.CR3    # what NINA decodes: model, raw size, frame left/top/width/height
python tools/sensor-table/sensor_table.py pixls "EOS R8"          # measure the best raw.pixls.us sample without changing the table
python tools/sensor-table/sensor_table.py recheck                 # after a NINA update: decode every recorded sample again
```

Run `recheck` when a NINA release updates FreeImage/LibRaw. Newer LibRaw versions know the masked borders of more R bodies, so the size NINA decodes for those cameras can shrink, for example from 6192×4060 to 6000×4000 for the EOS R50. `recheck` lists every entry whose size changed. Update each one with `add --pixls` or `add --decoded`. It downloads every CR3 sample in full, several hundred MB, so use `--only "EOS R50"` to check a single camera.
