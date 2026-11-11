#!/usr/bin/env python3
"""Static field writes read straight from a .NET assembly's metadata and IL bytes.

The facts layer (ilfacts.py) reads an `ikdasm` dump, which for the test
assembly costs about 20 s to disassemble and 30 s to parse. The lifetime gate
(lifetimes.py) needs one thing from Parsek.Tests.dll: which statics of
another assembly its code stores (`stsfld`), takes the address of
(`ldsflda`) or sets through a static property setter (`call set_X`). This
module reads that from the PE file itself (ECMA-335 partition II: the
metadata tables, the method bodies, the opcode operand sizes) in a second or
two, with no disassembler.

    python3 scripts/arch/clrmeta.py Source/Parsek.Tests/bin/Debug/net472/Parsek.Tests.dll

`static_access(path)` returns one row per (writing type, opcode, target):
the writing type is the TypeDef path of the method that holds the
instruction (namespace plus nesting, `/` between nested names, generic arity
dropped: the facts layer's spelling), the target is `assembly|Type::name`
with `assembly` None for a field of the scanned assembly itself. A target
whose declaring type is a generic instantiation (a TypeSpec parent) is
counted in `unresolved`, not returned.

Everything is pure over the bytes except `read_assembly`. Plain stdlib.
"""

from __future__ import annotations

import argparse
import struct
import sys
from collections import Counter
from pathlib import Path

# Table numbers (II.22).
MODULE, TYPEREF, TYPEDEF, FIELDPTR, FIELD, METHODPTR, METHODDEF = 0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06
MEMBERREF, MODULEREF, TYPESPEC, ASSEMBLYREF, NESTEDCLASS = 0x0A, 0x1A, 0x1B, 0x23, 0x29

# Coded indexes (II.24.2.6): tag bits and the tables they select (None: unused tag).
CODED = {
    "TypeDefOrRef": (2, (TYPEDEF, TYPEREF, TYPESPEC)),
    "HasConstant": (2, (FIELD, 0x08, 0x17)),
    "HasCustomAttribute": (5, (METHODDEF, FIELD, TYPEREF, TYPEDEF, 0x08, 0x09, MEMBERREF, MODULE, 0x0E, 0x17,
                               0x14, 0x11, MODULEREF, TYPESPEC, 0x20, ASSEMBLYREF, 0x26, 0x27, 0x28, 0x2A,
                               0x2C, 0x2B)),
    "HasFieldMarshal": (1, (FIELD, 0x08)),
    "HasDeclSecurity": (2, (TYPEDEF, METHODDEF, 0x20)),
    "MemberRefParent": (3, (TYPEDEF, TYPEREF, MODULEREF, METHODDEF, TYPESPEC)),
    "HasSemantics": (1, (0x14, 0x17)),
    "MethodDefOrRef": (1, (METHODDEF, MEMBERREF)),
    "MemberForwarded": (1, (FIELD, METHODDEF)),
    "Implementation": (2, (0x26, ASSEMBLYREF, 0x27)),
    "CustomAttributeType": (3, (None, None, METHODDEF, MEMBERREF, None)),
    "ResolutionScope": (2, (MODULE, MODULEREF, ASSEMBLYREF, TYPEREF)),
    "TypeOrMethodDef": (1, (TYPEDEF, METHODDEF)),
}

# Column kinds per table: "u2" / "u4" constants, heap indexes ("s" string,
# "g" guid, "b" blob), a table number (a simple index) or a coded index name.
SCHEMA = {
    0x00: ("u2", "s", "g", "g", "g"),
    0x01: ("ResolutionScope", "s", "s"),
    0x02: ("u4", "s", "s", "TypeDefOrRef", FIELD, METHODDEF),
    0x03: (FIELD,),
    0x04: ("u2", "s", "b"),
    0x05: (METHODDEF,),
    0x06: ("u4", "u2", "u2", "s", "b", 0x08),
    0x07: (0x08,),
    0x08: ("u2", "u2", "s"),
    0x09: (TYPEDEF, "TypeDefOrRef"),
    0x0A: ("MemberRefParent", "s", "b"),
    0x0B: ("u2", "HasConstant", "b"),
    0x0C: ("HasCustomAttribute", "CustomAttributeType", "b"),
    0x0D: ("HasFieldMarshal", "b"),
    0x0E: ("u2", "HasDeclSecurity", "b"),
    0x0F: ("u2", "u4", TYPEDEF),
    0x10: ("u4", FIELD),
    0x11: ("b",),
    0x12: (TYPEDEF, 0x14),
    0x13: (0x14,),
    0x14: ("u2", "s", "TypeDefOrRef"),
    0x15: (TYPEDEF, 0x17),
    0x16: (0x17,),
    0x17: ("u2", "s", "b"),
    0x18: ("u2", METHODDEF, "HasSemantics"),
    0x19: (TYPEDEF, "MethodDefOrRef", "MethodDefOrRef"),
    0x1A: ("s",),
    0x1B: ("b",),
    0x1C: ("u2", "MemberForwarded", "s", MODULEREF),
    0x1D: ("u4", FIELD),
    0x1E: ("u4", "u4"),
    0x1F: ("u4",),
    0x20: ("u4", "u2", "u2", "u2", "u2", "u4", "b", "s", "s"),
    0x21: ("u4",),
    0x22: ("u4", "u4", "u4"),
    0x23: ("u2", "u2", "u2", "u2", "u4", "b", "s", "s", "b"),
    0x24: ("u4", ASSEMBLYREF),
    0x25: ("u4", "u4", "u4", ASSEMBLYREF),
    0x26: ("u4", "s", "b"),
    0x27: ("u4", "u4", "s", "s", "Implementation"),
    0x28: ("u4", "u4", "s", "Implementation"),
    0x29: (TYPEDEF, TYPEDEF),
    0x2A: ("u2", "u2", "TypeOrMethodDef", "s"),
    0x2B: ("MethodDefOrRef", "b"),
    0x2C: (0x2A, "TypeDefOrRef"),
}

# Operand sizes (III.1.2, III.2, III.3, III.4). Everything not listed has none.
ONE_BYTE_OPERAND = {}
for _op in (0x0E, 0x0F, 0x10, 0x11, 0x12, 0x13, 0x1F, 0xDE) + tuple(range(0x2B, 0x38)):
    ONE_BYTE_OPERAND[_op] = 1
for _op in (0x20, 0x22, 0x27, 0x28, 0x29, 0x79, 0x8C, 0x8D, 0x8F, 0xA3, 0xA4, 0xA5, 0xC2, 0xC6, 0xD0, 0xDD) \
        + tuple(range(0x38, 0x45)) + tuple(range(0x6F, 0x76)) + tuple(range(0x7B, 0x82)):
    ONE_BYTE_OPERAND[_op] = 4
ONE_BYTE_OPERAND[0x21] = 8
ONE_BYTE_OPERAND[0x23] = 8
SWITCH = 0x45
TWO_BYTE_PREFIX = 0xFE
TWO_BYTE_OPERAND = {0x06: 4, 0x07: 4, 0x15: 4, 0x16: 4, 0x1C: 4, 0x12: 1, 0x19: 1,
                    0x09: 2, 0x0A: 2, 0x0B: 2, 0x0C: 2, 0x0D: 2, 0x0E: 2}

LDSFLDA, STSFLD, CALL, CALLVIRT = 0x7F, 0x80, 0x28, 0x6F
OPCODE_NAMES = {LDSFLDA: "ldsflda", STSFLD: "stsfld", CALL: "call", CALLVIRT: "callvirt"}
WATCHED = frozenset(OPCODE_NAMES)


class FormatError(ValueError):
    """The bytes are not a .NET assembly this reader understands."""


def walk_code(code, watched=WATCHED):
    """(offset, opcode, 4-byte operand) for each one-byte opcode in `watched`.

    The walk decodes every instruction (its operand size, `switch`'s jump
    table, the 0xFE two-byte opcodes), so an operand byte is never read as
    an opcode.
    """
    found = []
    pos = 0
    end = len(code)
    while pos < end:
        op = code[pos]
        pos += 1
        if op == TWO_BYTE_PREFIX:
            if pos >= end:
                raise FormatError("truncated two-byte opcode")
            pos += 1 + TWO_BYTE_OPERAND.get(code[pos], 0)
            continue
        if op == SWITCH:
            if pos + 4 > end:
                raise FormatError("truncated switch")
            count = struct.unpack_from("<I", code, pos)[0]
            pos += 4 + 4 * count
            continue
        size = ONE_BYTE_OPERAND.get(op, 0)
        if pos + size > end:
            raise FormatError("an instruction runs past the end of its method body")
        if op in watched:
            found.append((pos - 1, op, struct.unpack_from("<I", code, pos)[0]))
        pos += size
    if pos != end:
        raise FormatError("an instruction runs past the end of its method body")
    return found


def method_code(data, offset):
    """The IL bytes of the body at file offset `offset` (tiny or fat header, II.25.4)."""
    head = data[offset]
    if head & 0x3 == 0x2:
        return data[offset + 1: offset + 1 + (head >> 2)]
    if head & 0x3 == 0x3:
        flags_size = struct.unpack_from("<H", data, offset)[0]
        header_size = (flags_size >> 12) * 4
        code_size = struct.unpack_from("<I", data, offset + 4)[0]
        return data[offset + header_size: offset + header_size + code_size]
    raise FormatError("method body at 0x%x has no tiny or fat header" % offset)


def strip_arity(name):
    return name.split("`", 1)[0]


class Assembly:
    """The metadata tables of one assembly (only what the static scan reads)."""

    def __init__(self, data):
        self.data = data
        self._read_pe()
        self._read_metadata()

    # --- PE ---------------------------------------------------------------------------

    def _read_pe(self):
        data = self.data
        if data[:2] != b"MZ":
            raise FormatError("no MZ header")
        pe = struct.unpack_from("<I", data, 0x3C)[0]
        if data[pe: pe + 4] != b"PE\0\0":
            raise FormatError("no PE signature")
        sections, = struct.unpack_from("<H", data, pe + 6)
        optional_size, = struct.unpack_from("<H", data, pe + 20)
        optional = pe + 24
        magic, = struct.unpack_from("<H", data, optional)
        directories = optional + (96 if magic == 0x10B else 112)
        cli_rva, _cli_size = struct.unpack_from("<II", data, directories + 14 * 8)
        self.sections = []
        table = optional + optional_size
        for index in range(sections):
            entry = table + 40 * index
            virtual_size, virtual_address, raw_size, raw_pointer = struct.unpack_from("<IIII", data, entry + 8)
            self.sections.append((virtual_address, max(virtual_size, raw_size), raw_pointer))
        if not cli_rva:
            raise FormatError("no CLI header (not a .NET assembly)")
        cli = self.offset(cli_rva)
        self.metadata_offset = self.offset(struct.unpack_from("<I", data, cli + 8)[0])

    def offset(self, rva):
        for virtual_address, size, raw_pointer in self.sections:
            if virtual_address <= rva < virtual_address + size:
                return rva - virtual_address + raw_pointer
        raise FormatError("RVA 0x%x is in no section" % rva)

    # --- metadata ---------------------------------------------------------------------

    def _read_metadata(self):
        data = self.data
        root = self.metadata_offset
        if struct.unpack_from("<I", data, root)[0] != 0x424A5342:
            raise FormatError("no metadata signature")
        version_length, = struct.unpack_from("<I", data, root + 12)
        pos = root + 16 + version_length
        _flags, count = struct.unpack_from("<HH", data, pos)
        pos += 4
        streams = {}
        for _ in range(count):
            offset, size = struct.unpack_from("<II", data, pos)
            pos += 8
            name_end = data.index(b"\0", pos)
            name = data[pos:name_end].decode("ascii")
            pos = (name_end + 4) & ~3
            streams[name] = (root + offset, size)
        if "#~" not in streams:
            raise FormatError("no #~ table stream (uncompressed #- metadata is not read)")
        self.strings_offset = streams["#Strings"][0]
        self.blob_offset = streams.get("#Blob", (0, 0))[0]
        tables = streams["#~"][0]
        heap_sizes = data[tables + 6]
        valid, = struct.unpack_from("<Q", data, tables + 8)
        pos = tables + 24
        self.rows = [0] * 64
        for number in range(64):
            if valid >> number & 1:
                self.rows[number], = struct.unpack_from("<I", data, pos)
                pos += 4
        string_size = 4 if heap_sizes & 1 else 2
        guid_size = 4 if heap_sizes & 2 else 2
        blob_size = 4 if heap_sizes & 4 else 2
        self.columns = {}
        self.table_offset = {}
        for number in range(64):
            if not self.rows[number]:
                continue
            if number not in SCHEMA:
                raise FormatError("metadata table 0x%02x is present but not modelled" % number)
            layout = []
            width = 0
            for kind in SCHEMA[number]:
                if kind == "s":
                    size = string_size
                elif kind == "g":
                    size = guid_size
                elif kind == "b":
                    size = blob_size
                elif kind in ("u2", "u4"):
                    size = int(kind[1])
                elif isinstance(kind, str):
                    bits, targets = CODED[kind]
                    biggest = max((self.rows[t] for t in targets if t is not None), default=0)
                    size = 2 if biggest < (1 << (16 - bits)) else 4
                else:
                    size = 2 if self.rows[kind] < (1 << 16) else 4
                layout.append((width, size, kind))
                width += size
            self.columns[number] = (layout, width)
            self.table_offset[number] = pos
            pos += width * self.rows[number]

    def cell(self, table, row, column):
        """Raw value of a column (row counted from 1)."""
        layout, width = self.columns[table]
        start, size, _kind = layout[column]
        at = self.table_offset[table] + (row - 1) * width + start
        return struct.unpack_from("<H" if size == 2 else "<I", self.data, at)[0]

    def string(self, index):
        start = self.strings_offset + index
        return self.data[start: self.data.index(b"\0", start)].decode("utf-8")

    def blob_first_byte(self, index):
        pos = self.blob_offset + index
        head = self.data[pos]
        skip = 1 if head < 0x80 else (2 if head < 0xC0 else 4)
        return self.data[pos + skip]

    @staticmethod
    def decode(coded, value):
        bits, targets = CODED[coded]
        return targets[value & ((1 << bits) - 1)], value >> bits

    # --- names --------------------------------------------------------------------------

    def typedef_paths(self):
        """{TypeDef row: path} with nesting resolved through NestedClass."""
        enclosing = {}
        for row in range(1, self.rows[NESTEDCLASS] + 1):
            enclosing[self.cell(NESTEDCLASS, row, 0)] = self.cell(NESTEDCLASS, row, 1)
        names = {}
        for row in range(1, self.rows[TYPEDEF] + 1):
            name = strip_arity(self.string(self.cell(TYPEDEF, row, 1)))
            namespace = self.string(self.cell(TYPEDEF, row, 2))
            names[row] = (namespace + "." + name) if namespace else name
        paths = {}
        for row in names:
            parts = [names[row]]
            seen = {row}
            outer = enclosing.get(row)
            while outer is not None and outer not in seen:
                parts.append(names[outer])
                seen.add(outer)
                outer = enclosing.get(outer)
            paths[row] = "/".join(reversed(parts))
        return paths

    def typeref_target(self, row, depth=0):
        """(assembly name or None, path) of a TypeRef row."""
        scope_table, scope_row = self.decode("ResolutionScope", self.cell(TYPEREF, row, 0))
        name = strip_arity(self.string(self.cell(TYPEREF, row, 1)))
        namespace = self.string(self.cell(TYPEREF, row, 2))
        own = (namespace + "." + name) if namespace else name
        if scope_table == TYPEREF and scope_row and depth < 16:
            assembly, outer = self.typeref_target(scope_row, depth + 1)
            return assembly, outer + "/" + own
        if scope_table == ASSEMBLYREF and scope_row:
            return self.string(self.cell(ASSEMBLYREF, scope_row, 6)), own
        return None, own

    def owner_rows(self, table, list_column, count):
        """{member row: TypeDef row} from a TypeDef list column (FieldList or MethodList)."""
        owner = {}
        types = self.rows[TYPEDEF]
        for row in range(1, types + 1):
            start = self.cell(TYPEDEF, row, list_column)
            stop = self.cell(TYPEDEF, row + 1, list_column) if row < types else count + 1
            for member in range(start, stop):
                owner[member] = row
        return owner


def static_access(data):
    """({(writer type, opcode name, target)}, Counter of what was skipped) for one assembly.

    `target` is `assembly|Type::name` for a field or static setter of another
    assembly, `None|Type::name` (assembly None) for a field of this one. A
    call counts only when its target is a static `set_` method of another
    assembly (a property setter: the field behind it is the facts layer's
    business).
    """
    meta = Assembly(data)
    if meta.rows[FIELDPTR] or meta.rows[METHODPTR]:
        raise FormatError("FieldPtr / MethodPtr indirection tables are not read")
    paths = meta.typedef_paths()
    field_owner = meta.owner_rows(FIELD, 4, meta.rows[FIELD])
    method_owner = meta.owner_rows(METHODDEF, 5, meta.rows[METHODDEF])
    typerefs = {}
    out = set()
    skipped = Counter()

    def member_target(row, want_field):
        parent_table, parent_row = meta.decode("MemberRefParent", meta.cell(MEMBERREF, row, 0))
        name = meta.string(meta.cell(MEMBERREF, row, 1))
        if parent_table != TYPEREF:
            skipped["memberRef parent %s" % ("TypeSpec" if parent_table == TYPESPEC else hex(parent_table))] += 1
            return None
        signature = meta.blob_first_byte(meta.cell(MEMBERREF, row, 2))
        is_field = signature == 0x06
        if want_field != is_field:
            return None
        if not want_field and (signature & 0x20 or not name.startswith("set_")):
            return None
        if parent_row not in typerefs:
            typerefs[parent_row] = meta.typeref_target(parent_row)
        assembly, path = typerefs[parent_row]
        return "%s|%s::%s" % (assembly, path, name)

    for row in range(1, meta.rows[METHODDEF] + 1):
        rva = meta.cell(METHODDEF, row, 0)
        if not rva:
            continue
        writer = paths.get(method_owner.get(row))
        code = method_code(meta.data, meta.offset(rva))
        for _offset, op, token in walk_code(code):
            table, index = token >> 24, token & 0xFFFFFF
            if op in (STSFLD, LDSFLDA):
                if table == FIELD:
                    target = "None|%s::%s" % (paths.get(field_owner.get(index)),
                                              meta.string(meta.cell(FIELD, index, 1)))
                elif table == MEMBERREF:
                    target = member_target(index, True)
                else:
                    skipped["field token table %s" % hex(table)] += 1
                    target = None
            elif table == MEMBERREF:
                target = member_target(index, False)
            else:
                target = None
            if target is not None:
                out.add((writer, OPCODE_NAMES[op], target))
    return out, skipped


def read_assembly(path):
    return Path(path).read_bytes()


def main(argv=None):
    parser = argparse.ArgumentParser(description="Static field writes read from an assembly's metadata.")
    parser.add_argument("assembly")
    parser.add_argument("--assembly-name", default="Parsek",
                        help="list only targets in this referenced assembly (default Parsek; 'self' for own fields)")
    args = parser.parse_args(argv)
    rows, skipped = static_access(read_assembly(args.assembly))
    want = "None" if args.assembly_name == "self" else args.assembly_name
    targets = Counter()
    for _writer, opcode, target in rows:
        if target.split("|", 1)[0] == want:
            targets[(opcode, target.split("|", 1)[1])] += 1
    for (opcode, target), n in sorted(targets.items()):
        print("%-8s %-90s %d writing types" % (opcode, target, n))
    print("%d distinct targets; skipped %s" % (len(targets), dict(skipped)))
    return 0


if __name__ == "__main__":
    sys.exit(main())
