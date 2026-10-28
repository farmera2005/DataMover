"""Lake names, the same rules as Lakeflow.Contracts.LakeNames (C#)."""
import re

DIVISIONS = ("grain", "feed", "agronomy", "erp", "other")


def identifier(text: str) -> str:
    """Lower case, letters/digits/underscores only, no repeated or edge underscores."""
    out = []
    for ch in text.strip().lower():
        c = ch if (ch.isascii() and ch.isalnum()) else "_"
        if c == "_" and (not out or out[-1] == "_"):
            continue
        out.append(c)
    ident = "".join(out).rstrip("_")
    if not ident:
        raise ValueError(f"'{text}' has no usable characters for a lake name.")
    return ident


def table_part(table: str) -> str:
    """'dbo.Tickets' -> 'tickets'; any schema other than dbo is kept: 'audit.Log' -> 'audit_log'."""
    schema, dot, name = table.partition(".")
    if not dot:
        schema, name = "", schema
    if not schema or schema.lower() == "dbo":
        return identifier(name)
    return f"{identifier(schema)}_{identifier(name)}"


def raw_table(division: str, bronze: str = "bronze") -> str:
    """Where Eventstream lands one division hub."""
    return f"{bronze}.{identifier(division)}_events"


def bronze_table(division: str, source_system: str, table: str, bronze: str = "bronze") -> str:
    return f"{bronze}.{identifier(division)}_{identifier(source_system)}_{table_part(table)}_changes"


def silver_table(source_system: str, table: str, silver: str = "silver") -> str:
    return f"{silver}.{identifier(source_system)}_{table_part(table)}"


_INVALID_COLUMN = re.compile(r"[ ,;{}()\n\t=.]")


def column_name(source_column: str) -> str:
    """
    A source column as a Delta column without column mapping: characters Delta rejects become '_', and a leading
    '_' gets a 'c' prefix so source columns never collide with Lakeflow's own '_' columns.
    """
    name = _INVALID_COLUMN.sub("_", source_column)
    return "c" + name if name.startswith("_") or not name else name
