"""Run after dotnet test to compare C# output with Python and native Windows IStorage."""

from pathlib import Path
import re
import sys
import tempfile

ROOT = Path(__file__).resolve().parents[1]
sys.path[:0] = [str(ROOT / "python"), str(ROOT / "python" / "tests")]

from decay_to_das import write_das
from csv_to_das import convert_file
from test_decay_to_das import read_compound, native_read_stream, string


def main():
    generated = ROOT / ".artifacts" / "python-parity"
    stream_count = 0
    with tempfile.TemporaryDirectory() as temporary:
        expected_dir = Path(temporary)
        for count in [2, 1021, 1022, 2048, 16384]:
            write_das(expected_dir / f"boundary-{count}.das", range(count), range(count),
                      prompt=range(count), weighting="poisson")
        write_das(expected_dir / "signed.das", [4, 4.5, 5], [-2.5, 0.25, 1.75],
                  time_unit="ns", name="試料 🌟")
        write_das(expected_dir / "resample.das", [0, 1, 3], [0, 10, 0],
                  resample=4, prompt=[0, 6, 0], prompt_time=[0, 1.5, 3])
        source = ROOT / "python" / "Y6 film-1exp.CSV"
        convert_file(source, expected_dir / "film.das")
        convert_file(source, expected_dir / "window.das", start_time=-2, end_time=20)
        for expected_path in sorted(expected_dir.glob("*.das")):
            actual_path = generated / expected_path.name
            actual, actual_storages = read_compound(actual_path)
            expected, expected_storages = read_compound(expected_path)
            assert actual_storages == expected_storages, actual_path
            assert actual.keys() == expected.keys(), actual_path
            for name, data in expected.items():
                if name.endswith("/Comment"):
                    # G17 and Python's .17g differ only in the exponent's letter case here.
                    normalized = re.sub(r"(?<=\d)E([+-]\d+)", r"e\1", string(actual[name]))
                    assert normalized == string(data), (actual_path.name, name, normalized, string(data))
                else:
                    assert actual[name] == data, (actual_path.name, name)
                if sys.platform == "win32":
                    assert native_read_stream(actual_path, name, len(actual[name])) == actual[name], (actual_path, name)
                stream_count += 1
            print(f"PASS {actual_path.name}: {len(actual)} matching streams")
    print(f"Verified 9 files and {stream_count} streams against Python" +
          (" and native Windows IStorage/IStream." if sys.platform == "win32" else "."))


if __name__ == "__main__":
    main()
