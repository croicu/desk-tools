"""Echo script for tests/Apps/McpSecrets (issue #50, slice 4b).

Not an MCP server -- Service does not know or care what it launches. It reads lines from stdin and,
for each one, answers with the value of the PROBE_SECRET environment variable (which Service sets
from the tool's declared secret), or "<not set>" if the variable is missing. Exits when stdin closes.
"""

import os
import sys


def main() -> int:
    for _ in sys.stdin:
        value = os.environ.get("PROBE_SECRET")
        sys.stdout.write("PROBE_SECRET=" + (value if value is not None else "<not set>") + "\n")
        sys.stdout.flush()
    return 0


if __name__ == "__main__":
    sys.exit(main())
