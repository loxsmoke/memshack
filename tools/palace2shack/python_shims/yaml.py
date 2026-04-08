from __future__ import annotations


def safe_load(stream):
    if hasattr(stream, "read"):
        text = stream.read()
    else:
        text = str(stream)
    return _Parser(text).parse()


class _Parser:
    def __init__(self, text: str):
        self._lines = []
        for raw_line in text.splitlines():
            if not raw_line.strip():
                continue
            stripped = raw_line.lstrip()
            if stripped.startswith("#"):
                continue
            indent = len(raw_line) - len(stripped)
            self._lines.append((indent, stripped.rstrip()))
        self._index = 0

    def parse(self):
        if not self._lines:
            return None
        return self._parse_block(self._lines[0][0])

    def _parse_block(self, indent: int):
        if self._index >= len(self._lines):
            return None
        _, content = self._lines[self._index]
        if content.startswith("- "):
            return self._parse_sequence(indent)
        return self._parse_mapping(indent)

    def _parse_mapping(self, indent: int):
        mapping = {}
        while self._index < len(self._lines):
            current_indent, content = self._lines[self._index]
            if current_indent < indent or current_indent != indent or content.startswith("- "):
                break

            key, separator, remainder = content.partition(":")
            if not separator:
                raise ValueError(f"Invalid YAML line: {content}")
            key = key.strip()
            remainder = remainder.strip()
            self._index += 1

            if remainder:
                mapping[key] = _parse_scalar(remainder)
                continue

            if self._index >= len(self._lines) or self._lines[self._index][0] <= current_indent:
                mapping[key] = None
                continue

            mapping[key] = self._parse_block(self._lines[self._index][0])

        return mapping

    def _parse_sequence(self, indent: int):
        items = []
        while self._index < len(self._lines):
            current_indent, content = self._lines[self._index]
            if current_indent < indent or current_indent != indent or not content.startswith("- "):
                break

            remainder = content[2:].strip()
            self._index += 1

            if not remainder:
                if self._index < len(self._lines) and self._lines[self._index][0] > current_indent:
                    items.append(self._parse_block(self._lines[self._index][0]))
                else:
                    items.append(None)
                continue

            if ":" in remainder:
                key, separator, value = remainder.partition(":")
                if separator:
                    entry = {key.strip(): _parse_scalar(value.strip()) if value.strip() else None}
                    if self._index < len(self._lines) and self._lines[self._index][0] > current_indent:
                        child = self._parse_block(self._lines[self._index][0])
                        if isinstance(child, dict):
                            entry.update(child)
                    items.append(entry)
                    continue

            items.append(_parse_scalar(remainder))

        return items


def _parse_scalar(value: str):
    if value == "" or value == "null":
        return None
    if value == "true":
        return True
    if value == "false":
        return False
    if (value.startswith('"') and value.endswith('"')) or (value.startswith("'") and value.endswith("'")):
        return value[1:-1]
    try:
        return int(value)
    except ValueError:
        pass
    try:
        return float(value)
    except ValueError:
        pass
    return value
