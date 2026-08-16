from __future__ import annotations

import argparse
import re
import sys
from pathlib import Path
from typing import Any, Optional, Union

import dnfile
from dnfile.enums import MetadataTables
from dnfile.mdtable import MethodDefRow
from dncil.cil.body import CilMethodBody
from dncil.cil.body.reader import CilMethodBodyReaderBase
from dncil.cil.error import MethodBodyFormatError
from dncil.clr.token import InvalidToken, StringToken, Token


DOTNET_META_TABLES_BY_INDEX = {table.value: table.name for table in MetadataTables}


class DnfileMethodBodyReader(CilMethodBodyReaderBase):
    def __init__(self, pe: dnfile.dnPE, row: MethodDefRow):
        self.pe = pe
        self.offset = self.pe.get_offset_from_rva(row.Rva)

    def read(self, size: int) -> bytes:
        data = self.pe.get_data(self.pe.get_rva_from_offset(self.offset), size)
        self.offset += size
        return data

    def tell(self) -> int:
        return self.offset

    def seek(self, offset: int) -> int:
        self.offset = offset
        return self.offset


def read_user_string(pe: dnfile.dnPE, token: StringToken) -> Union[str, InvalidToken]:
    try:
        value: Optional[dnfile.stream.UserString] = pe.net.user_strings.get(token.rid)
    except UnicodeDecodeError:
        return InvalidToken(token.value)
    if value is None or isinstance(value, bytes) or value.value is None:
        return InvalidToken(token.value)
    return value.value


def resolve_token(pe: dnfile.dnPE, token: Token) -> Any:
    if isinstance(token, StringToken):
        return read_user_string(pe, token)
    table_name = DOTNET_META_TABLES_BY_INDEX.get(token.table, "")
    table = getattr(pe.net.mdtables, table_name, None) if table_name else None
    if table is None:
        return InvalidToken(token.value)
    try:
        return table.rows[token.rid - 1]
    except IndexError:
        return InvalidToken(token.value)


def type_name(row: Any) -> str:
    namespace = str(getattr(row, "TypeNamespace", ""))
    name = str(getattr(row, "TypeName", ""))
    return f"{namespace}.{name}" if namespace else name


def format_operand(pe: dnfile.dnPE, operand: Any) -> str:
    if isinstance(operand, Token):
        operand = resolve_token(pe, operand)
    if isinstance(operand, str):
        return repr(operand)
    if isinstance(operand, int):
        return hex(operand)
    if isinstance(operand, list):
        return "[" + ", ".join(f"IL_{offset:04X}" for offset in operand) + "]"
    if isinstance(operand, dnfile.mdtable.MemberRefRow):
        owner = operand.Class.row
        if isinstance(owner, (dnfile.mdtable.TypeRefRow, dnfile.mdtable.TypeDefRow)):
            return f"{type_name(owner)}::{operand.Name}"
        return str(operand.Name)
    if isinstance(operand, dnfile.mdtable.TypeRefRow):
        return type_name(operand)
    if isinstance(operand, dnfile.mdtable.TypeDefRow):
        return type_name(operand)
    if isinstance(operand, (dnfile.mdtable.FieldRow, dnfile.mdtable.MethodDefRow)):
        return str(operand.Name)
    if operand is None:
        return ""
    return str(operand)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("assembly", type=Path)
    parser.add_argument("--type", dest="type_pattern", required=True)
    parser.add_argument("--method", dest="method_pattern", default=".*")
    args = parser.parse_args()

    type_re = re.compile(args.type_pattern, re.IGNORECASE)
    method_re = re.compile(args.method_pattern, re.IGNORECASE)
    pe = dnfile.dnPE(str(args.assembly))

    matches = 0
    for type_row in pe.net.mdtables.TypeDef:
        owner = type_name(type_row)
        if not type_re.search(owner):
            continue
        for method_index in type_row.MethodList:
            row = method_index.row
            if row is None or not method_re.search(str(row.Name)):
                continue
            if not row.ImplFlags.miIL or row.Flags.mdAbstract or row.Flags.mdPinvokeImpl or row.Rva == 0:
                continue
            try:
                body = CilMethodBody(DnfileMethodBodyReader(pe, row))
            except MethodBodyFormatError as error:
                print(f"\n{owner}::{row.Name}: {error}")
                continue
            if not body.instructions:
                continue
            matches += 1
            print(f"\nTYPE {owner}\nMETHOD {row.Name} RVA=0x{row.Rva:X}")
            for instruction in body.instructions:
                print(
                    f"IL_{instruction.offset:04X}  "
                    f"{instruction.mnemonic:<14} "
                    f"{format_operand(pe, instruction.operand)}"
                )

    if matches == 0:
        print("No matching recovered IL methods found.", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
