#!/usr/bin/env python3
"""Heurística de "usado mas não declarado" / "declarado duas vezes" para C#.

Não substitui o compilador (não há .NET no ambiente): procura as classes de erro que já
apareceram de verdade neste port (CS0103 campo usado sem declaração, CS0102 membro duplicado)
para não depender de descobri-las uma por rodada de CI.
"""
import re
import sys
from pathlib import Path

KEYWORDS = {
    'abstract', 'add', 'and', 'as', 'async', 'await', 'base', 'bool', 'break', 'byte', 'case',
    'catch', 'char', 'checked', 'class', 'const', 'continue', 'decimal', 'default', 'delegate',
    'do', 'double', 'dynamic', 'else', 'enum', 'event', 'explicit', 'extern', 'false', 'file',
    'finally', 'fixed', 'float', 'for', 'foreach', 'get', 'global', 'goto', 'if', 'implicit',
    'in', 'init', 'int', 'interface', 'internal', 'is', 'lock', 'long', 'nameof', 'namespace',
    'new', 'not', 'notnull', 'null', 'object', 'operator', 'or', 'out', 'override', 'params',
    'partial', 'private', 'protected', 'public', 'readonly', 'record', 'ref', 'remove', 'required',
    'return', 'sbyte', 'scoped', 'sealed', 'set', 'short', 'sizeof', 'stackalloc', 'static',
    'string', 'struct', 'switch', 'this', 'throw', 'true', 'try', 'typeof', 'uint', 'ulong',
    'unchecked', 'unmanaged', 'unsafe', 'ushort', 'using', 'value', 'var', 'virtual', 'void',
    'volatile', 'when', 'where', 'while', 'with', 'yield', '_',
}

MODS = r'(?:public|private|protected|internal|static|readonly|const|sealed|abstract|override|virtual|new|unsafe|extern|async|partial|volatile|ref|required|in|out)'


def strip_directives(src: str) -> str:
    return '\n'.join('' if line.lstrip().startswith('#') else line for line in src.split('\n'))


def strip_code(src: str) -> str:
    """Remove comentários e strings (interpoladas inclusive) para não confundir a análise."""
    out, i, n = [], 0, len(src)
    while i < n:
        c = src[i]
        if c == '/' and i + 1 < n and src[i + 1] == '/':
            while i < n and src[i] != '\n':
                i += 1
        elif c == '/' and i + 1 < n and src[i + 1] == '*':
            i += 2
            while i + 1 < n and not (src[i] == '*' and src[i + 1] == '/'):
                i += 1
            i += 2
            out.append(' ')
        elif c in '"\'@$':
            j = i
            while j < n and src[j] in '@$':
                j += 1
            if j < n and src[j] == '"':
                interpolated = j != i
                i = j + 1
                depth_ = 0
                while i < n:
                    ch = src[i]
                    if ch == '\\':
                        i += 2
                        continue
                    if interpolated and ch == '{':
                        depth_ += 1
                        i += 1
                        continue
                    if interpolated and ch == '}' and depth_ > 0:
                        depth_ -= 1
                        i += 1
                        continue
                    if ch == '"':
                        if depth_ > 0:      # string aninhada dentro da interpolação
                            i += 1
                            while i < n and src[i] != '"':
                                i += 2 if src[i] == '\\' else 1
                            i += 1
                            continue
                        if i + 1 < n and src[i + 1] == '"':
                            i += 2
                            continue
                        i += 1
                        break
                    i += 1
                out.append(' "" ')
            elif j < n and src[j] == "'":
                i = j + 1
                while i < n:
                    if src[i] == '\\':
                        i += 2
                        continue
                    if src[i] == "'":
                        i += 1
                        break
                    i += 1
                out.append(" '' ")
            else:
                out.append(c)
                i += 1
        else:
            out.append(c)
            i += 1
    return ''.join(out)


def split_top_level(text: str):
    parts, depth, current = [], 0, []
    for ch in text:
        if ch in '(<[':
            depth += 1
        elif ch in ')>]':
            depth -= 1
        if ch == ',' and depth == 0:
            parts.append(''.join(current))
            current = []
        else:
            current.append(ch)
    parts.append(''.join(current))
    return parts


def declared_names(code: str) -> set:
    names = set()
    # tipos
    for m in re.finditer(r'\b(?:class|struct|record|interface|enum)\s+([A-Za-z_]\w*)', code):
        names.add(m.group(1))
    # "Tipo nome" (campos, locais, parâmetros, catch, tuplas) — tipos comuns e primitivos
    types = (r'(?:var|[A-Z][\w<>?\[\],\.]*|float|double|decimal|int|uint|long|ulong|short|ushort|'
             r'byte|sbyte|bool|char|string|object|dynamic)')
    for m in re.finditer(types + r'\[?\]?\??\s+(\w+)\b', code):
        names.add(m.group(1))
    # "Tipo<...> nome" com genéricos aninhados: Action<string, float> progress, IEnumerable<(..)> source
    for idx, ch in enumerate(code):
        if ch != '<':
            continue
        depth, end = 0, -1
        for k in range(idx, len(code)):
            if code[k] == '<':
                depth += 1
            elif code[k] == '>':
                depth -= 1
                if depth == 0:
                    end = k
                    break
        if end < 0 or not re.search(r'[A-Z]\w*\s*$', code[:idx]):
            continue
        m_next = re.match(r'\s+([a-z_]\w*)', code[end + 1:])
        if m_next:
            names.add(m_next.group(1))
    # parâmetro de indexador: this[int index]
    for m in re.finditer(r'\[\s*' + types + r'\[?\]?\??\s+(\w+)\s*\]', code):
        names.add(m.group(1))
    # parâmetros/lambdas: parênteses balanceados, inclusive com tipos de tupla dentro.
    # Só listas seguidas de '{' ou '=>' são declarações (chamadas de método terminam em ';').
    for m in re.finditer(r'\(', code):
        start = m.end() - 1
        depth = 0
        end = -1
        for k in range(start, len(code)):
            if code[k] == '(':
                depth += 1
            elif code[k] == ')':
                depth -= 1
                if depth == 0:
                    end = k
                    break
        if end < 0:
            continue
        after = code[end + 1:end + 4].strip()
        if not after.startswith(('{', '=>')):
            continue
        inner = code[start + 1:end]
        for part in split_top_level(inner):
            words = re.findall(r'[A-Za-z_]\w*', part)
            if words:
                names.add(words[-1])
    for m in re.finditer(r'\}\s*(\w+)\b', code):     # variável de pattern: { X: not null } tie
        names.add(m.group(1))
    # várias declarações numa linha: int r = 0, g = 0, b = 0;
    for line in code.split('\n'):
        if re.match(r'\s*(?:' + types + r')(?:\[\]|\?)?\s+\w+\s*=', line):
            for m in re.finditer(r'(?:^|[,\(])\s*(\w+)\s*=', line):
                names.add(m.group(1))
    for m in re.finditer(r'\b(\w+)\s*=>', code):
        names.add(m.group(1))
    for m in re.finditer(r'\bforeach\s*\(([^)]*)\)', code):
        for w in re.findall(r'[A-Za-z_]\w*', m.group(1).split(' in ')[0]):
            names.add(w)
    for m in re.finditer(r'\bis\s+[\w<>?\[\],]+\s+(\w+)\b', code):
        names.add(m.group(1))
    return names


def bare_lowercase(code: str) -> dict:
    """Identificadores em minúscula usados sem ponto antes (candidatos a campo/local)."""
    used = {}
    for m in re.finditer(r'(?<![\w.])([a-z_]\w*)\b', code):
        name = m.group(1)
        if name in KEYWORDS:
            continue
        if re.match(r'\s*:', code[m.end():]):   # argumento nomeado / rótulo
            continue
        line = code.count('\n', 0, m.start()) + 1
        used.setdefault(name, line)
    return used


def duplicate_members(code: str):
    """Membros declarados mais de uma vez no mesmo tipo (CS0102, CS0111)."""
    problems = []
    stack = []
    depth = 0
    for ln, line in enumerate(code.split('\n'), 1):
        stripped = line.strip()
        m = re.search(r'\b(?:class|struct|record|interface)\s+([A-Za-z_]\w*)', stripped)
        if m:
            stack.append([m.group(1), depth + 1, {}])
        for ch in line:
            if ch == '{':
                depth += 1
            elif ch == '}':
                if stack and depth == stack[-1][1]:
                    name, _, members = stack.pop()
                    for member, info in members.items():
                        kinds = {k for k, _ in info}
                        # sobrecarga legítima: mais de um método com assinaturas diferentes
                        if len(info) > 1 and kinds != {'method'}:
                            problems.append(f'{name}.{member} (linhas {[l for _, l in info]})')
                if depth > 0:
                    depth -= 1
        if stack and depth == stack[-1][1]:
            text = stripped.rstrip(';')
            method = re.match(rf'^(?:{MODS}\s+)*(?:[\w<>?\[\],\.]+\s+)?(\w+)\s*\(', text)
            expr = re.match(rf'^(?:{MODS}\s+)*(?:[\w<>?\[\],\.]+\s+)?(\w+)\s*=>', text)
            field = re.match(rf'^(?:{MODS}\s+)*[\w<>?\[\],\.]+\s+(\w+)\s*(?:=|;)', text)
            if method and not text.startswith(('if', 'for', 'while', 'switch', 'return', 'throw', 'using', 'lock', 'new', 'foreach', 'catch', 'else')):
                stack[-1][2].setdefault(method.group(1), []).append(('method', ln))
                continue
            for m2, kind in ((expr, 'method'), (field, 'field')):
                if m2:
                    stack[-1][2].setdefault(m2.group(1), []).append((kind, ln))
                    break
    return problems


def touch_control_wiring(root='src'):
    """Checagens do pad: um controle novo esquecido em algum lugar não dá erro de compilação,
    ele só não funciona (foi o caso do botão de teclado, desenhado mas fora do HitTest)."""
    host = Path(root) / 'Shared' / 'HostConfig.cs'
    pad = Path(root) / 'Celeste.Android.Patches' / 'Touch' / 'TouchControls.cs'
    if not host.is_file() or not pad.is_file():
        return []
    problems = []

    host_code = strip_code(strip_directives(host.read_text(encoding='utf-8-sig')))
    enum = re.search(r'enum TouchControl\s*\{(.*?)\}', host_code, re.S)
    if not enum:
        return ['TouchControl: enum não encontrado em ' + str(host)]
    members = dict((m.group(1), int(m.group(2))) for m in re.finditer(r'(\w+)\s*=\s*(\d+)', enum.group(1)))
    count = re.search(r'const int Count = (\d+)', host_code)
    if not count:
        return ['TouchLayoutSpec.Count não encontrado']
    count = int(count.group(1))

    # 1) Count precisa cobrir todos os valores do enum (índices são usados como posição no layout)
    for name, value in sorted(members.items(), key=lambda kv: kv[1]):
        if value >= count:
            problems.append(f'HostConfig: {name} = {value} mas TouchLayoutSpec.Count = {count}')

    # 2) o layout de fábrica precisa de uma posição por controle
    default = re.search(r'static TouchLayoutSpec Default \{ get; \} = new TouchLayoutSpec\((.*?)\);', host_code, re.S)
    if default:
        positions = len(re.findall(r'new TouchControlSpec\(', default.group(1)))
        if positions != count:
            problems.append(f'HostConfig: Default tem {positions} posições, Count = {count}')

    # 3) todo controle precisa ser reconhecido pelo HitTest, senão o toque nele é ignorado
    pad_code = strip_code(strip_directives(pad.read_text(encoding='utf-8-sig')))
    hit = re.search(r'public Control HitTest\(', pad_code)
    if hit:
        depth, end = 0, None
        for i in range(hit.end() - 1, len(pad_code)):
            if pad_code[i] == '{':
                depth += 1
            elif pad_code[i] == '}':
                depth -= 1
                if depth == 0:
                    end = i
                    break
        body = pad_code[hit.end():end] if end else pad_code[hit.end():]
        for name in members:
            if f'TouchControl.{name}' not in body:
                problems.append(f'HitTest: TouchControl.{name} nunca é atingido (o toque nesse controle é ignorado)')
    return problems


def main(paths):
    bad = 0
    extra = touch_control_wiring()
    for problem in extra:
        print(f'--- {problem}')
    if extra:
        bad += 1
    for path in paths:
        src = strip_directives(Path(path).read_text(encoding='utf-8-sig'))
        code = strip_code(src)
        declared = declared_names(code)
        used = bare_lowercase(code)
        unknown = {n: l for n, l in used.items() if n not in declared}
        dups = duplicate_members(code)
        if unknown or dups:
            bad += 1
            print(f'--- {path}')
            for n, l in sorted(unknown.items(), key=lambda kv: kv[1]):
                print(f'   linha {l}: "{n}" usado mas não declarado no arquivo')
            for d in dups:
                print(f'   membro duplicado: {d}')
    print('arquivos com suspeitas:', bad)
    return 1 if bad else 0


if __name__ == '__main__':
    files = sys.argv[1:] or sorted(str(p) for p in Path('src').rglob('*.cs'))
    sys.exit(main(files))
