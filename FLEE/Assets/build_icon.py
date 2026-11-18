"""Package the original artwork as a Windows icon; requires Pillow."""

from pathlib import Path

from PIL import Image


def main():
    assets = Path(__file__).resolve().parent
    sizes = [(size, size) for size in (16, 20, 24, 32, 40, 48, 64, 128, 256)]
    with Image.open(assets / "Flee.png") as artwork:
        artwork.save(assets / "Flee.ico", format="ICO", sizes=sizes)


if __name__ == "__main__":
    main()
