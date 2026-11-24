# Avisos de terceiros

## Bloxkeep — roblox-account-manager

O visual das categorias **Contas** e **Plus** (paleta, raios, tipografia e regras de
componentes: botão primário invertido, verde só para "vivo/ir", status como ponto de 7 px
ou trilho de 4 px mais uma palavra, nunca fundo colorido atrás de texto, linhas compactas
com divisórias finas, chips para filtrar, controle segmentado para trocar de vista e menus
com item destrutivo por último) foi adaptado do design system do Bloxkeep:

- `DESIGN.md`
- `RobloxManager/wwwroot/app.css` (tokens em `:root`)

Fonte: https://github.com/Bloxkeep/roblox-account-manager

Nenhum código foi copiado: o Bloxkeep é CSS/Rust e o RbxSwitcher é WinForms. Os tokens foram
traduzidos para cores sólidas em `src/RbxSwitcher.App/UI/Theme.cs` e os componentes foram
reescritos em `src/RbxSwitcher.App/UI/Controls.cs`. O aviso abaixo é preservado conforme a
licença.

```
MIT License

Copyright (c) 2026 minhhoang0122

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```
