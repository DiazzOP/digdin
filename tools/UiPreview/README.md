# UiPreview — prints das telas sem Windows

Ferramenta de desenvolvimento (não vai no app). Abre a janela real do RbxSwitcher com **contas e dados fictícios**
dentro do Wine e tira prints de Contas, Plus (lista, ordenada, detalhes), Avatares (sem catálogo e com catálogo),
janela de lote e janela de abrir o Roblox. Nada acessa o Roblox de verdade: as sessões são falsas e a rede falha.

## Preparar (Ubuntu, uma vez)

```bash
dpkg --add-architecture i386 && apt-get update && apt-get install -y wine64 wine32:i386 imagemagick xvfb
mkdir -p /run/user/0 && chmod 700 /run/user/0          # sem isso o wineserver 9.0 aborta (free(): invalid pointer)
export WINEPREFIX=/root/.wine XDG_RUNTIME_DIR=/run/user/0 WINEDLLOVERRIDES="mshtml="   # não desligue o mscoree: o .NET precisa dele
xvfb-run -a wine wineboot -i
```

Fontes parecidas com as do Windows (Segoe UI não é livre), num `.reg` importado com `wine regedit /S`:

```
[HKEY_CURRENT_USER\Software\Wine\Fonts\Replacements]
"Segoe UI"="Liberation Sans"
"Segoe UI Semibold"="Inter SemiBold"
[HKEY_CURRENT_USER\Software\Wine\WineDbg]
"ShowCrashDialog"=dword:00000000
```

## Rodar

```bash
dotnet publish tools/UiPreview/UiPreview.csproj -c Release -r win-x64 --self-contained -o /root/rbxprev
tools/UiPreview/shoot.sh antes 1366x768 96      # 100 %
tools/UiPreview/shoot.sh antes-150 1920x1080 144 # 150 %
```

Os prints ficam em `/root/shots/<nome>/`. O app pede cada print gravando `ready-<tela>` e o script captura a
tela virtual com o ImageMagick (a captura de dentro do Wine sai preta).

## Limites (o que o Wine não mostra igual ao Windows)

- Fontes substitutas: larguras um pouco diferentes; dígitos às vezes aparecem riscados (artefato do Wine).
- Barras de rolagem do sistema ficam claras no Wine; no Windows 10/11 o app pede as escuras.
- Sem barra de título (não há gerenciador de janelas no Xvfb). WebView2 não roda: a área do navegador fica vazia.
