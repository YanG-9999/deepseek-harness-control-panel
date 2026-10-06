# Tray typography

The four `TrayUI*.ttf.gz` resources are static instances of Noto Sans SC at
weights 400 (Regular), 500 (Medium), 600 (Semibold), and 700 (Bold).
They use independent family names `DSH Tray UI 400/500/600/700` so WinForms
does not collapse the intermediate weights to Regular/Bold or synthesize them.
The fonts are loaded privately in the process; nothing is installed into Windows.

Source: Google Fonts `ofl/notosanssc/NotoSansSC[wght].ttf`, revision
`a85815a42757630ce188fdad368c2dfc444d4773`.
Source SHA-256: `a3041811a78c361b1de50f953c805e0244951c21c5bd412f7232ef0d899af0da`.
License: SIL Open Font License 1.1, reproduced in `OFL.txt` and embedded in the EXE.
The panel remains MIT licensed; these font assets retain their OFL license.

To keep the executable small, the subset includes Latin-1, GB2312 characters and
all characters in the source file. Rare characters in external installation paths
may use Windows font fallback. Logs retain the system Consolas font.
This is a chosen replacement font: the reference image did not specify a family.

Rebuild assets only when needed:

```
python tools/PrepareTrayFonts.py path/to/NotoSansSC-VF.ttf
```

The tool needs `fonttools==4.60.1`; normal build/test/installation does not require
Python, FontTools, a font download, administrator privileges, or network access.
