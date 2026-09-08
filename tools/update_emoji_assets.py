"""Refresh the pinned, unmodified Twemoji PNG resources (Python standard library only)."""
import argparse
import hashlib
import io
import pathlib
import urllib.request
import zipfile

VERSION = "17.0.3"
COMMIT = "b6b55fef1e8636b540a6d016a4729ca8cdf2e60b"
ROOT = pathlib.Path(__file__).resolve().parents[1]


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--archive", type=pathlib.Path, help="Use an already downloaded upstream archive")
    args = parser.parse_args()
    data = args.archive.read_bytes() if args.archive else urllib.request.urlopen(
        f"https://codeload.github.com/jdecked/twemoji/zip/{COMMIT}", timeout=60).read()
    archive = zipfile.ZipFile(io.BytesIO(data))
    prefix = f"twemoji-{COMMIT}/"
    assets = sorted(n for n in archive.namelist() if n.startswith(prefix + "assets/72x72/") and n.endswith(".png"))
    if len(assets) != 4009:
        raise ValueError("Pinned asset count changed; inspect the upstream archive")
    destination = ROOT / "Shikari/Resources/Emoji"
    destination.mkdir(parents=True, exist_ok=True)
    index = []
    checksums = []
    for name in assets:
        filename = pathlib.PurePosixPath(name).name
        content = archive.read(name)
        (destination / filename).write_bytes(content)
        index.append(filename[:-4])
        checksums.append(f"{hashlib.sha256(content).hexdigest()}  {filename}")
    (destination / "index.txt").write_text("\n".join(index) + "\n", encoding="utf-8", newline="\n")
    (destination / "SHA256SUMS").write_text("\n".join(checksums) + "\n", encoding="utf-8", newline="\n")
    (destination / "LICENSE-GRAPHICS.txt").write_bytes(archive.read(prefix + "LICENSE-GRAPHICS"))
    print(f"Installed {len(index)} unmodified Twemoji {VERSION} PNGs from {COMMIT}.")


if __name__ == "__main__":
    main()
