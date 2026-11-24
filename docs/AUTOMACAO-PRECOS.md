# Automação de preços — contexto para continuar (escrito em 20/11/2026, versão 0.7.5)

**Estado: em pausa por decisão do usuário.** Nada foi construído para automação. Este documento junta o que já se
sabe para retomar num chat novo sem perder contexto.

## O que existe hoje (0.7.5)
- Aba **Limiteds** (só leitura): todas as cópias de todas as contas, por conta ou por item; situação (superada · Nº,
  à venda · 1º, pronta, em holding, sem Plus, vendida), meu preço, menor de outro vendedor, RAP · vendas/dia (ou
  demanda da Rolimons), líquido, cota de 30 dias por conta, sugestão de preço com piso, "Vender"/"Mudar preço" que
  abrem o item na conta certa e copiam a sugestão. Detalhes em `docs/LIMITEDS-VENDA.md`.
- Sugestão atual: `menor preço de outro − SellUndercut` (padrão 1), nunca abaixo de `SellFloorPercent` do RAP (padrão
  80 %); sem vendedores, valor Rolimons ou RAP. Código: `SellBoard.Suggest` em `src/RbxSwitcher.Core/Limiteds/Selling.cs`.
- Leituras: `SellReader` (`src/RbxSwitcher.Core/Limiteds/SellReader.cs`) e `RobloxApi.Selling.cs`; orquestração em
  `src/RbxSwitcher.App/MainForm.Selling.cs` (uma conta por vez, pausa de 429, automático a cada 15 min).

## Endpoints confirmados pela gravação da página do item (19/11, `venda-chamadas.txt`)
Itens do Marketplace (todos os testados tinham `collectibleItemId`, obtido em `catalog.roblox.com/v1/catalog/items/details`):
- `GET apis.roblox.com/marketplace-sales/v1/item/{cid}/resellable-instances?ownerType=User&ownerId={id}&limit=…&cursor=…`
  → `itemInstances[]`: `collectibleInstanceId`, `collectibleProductId`, `isHeld`, `price`, `saleState`, `serialNumber` (nulo).
- `GET …/item/{cid}/resellers?limit=100&cursor=…` → `data[]`: `seller.sellerId`, `price`, `collectibleItemInstanceId`,
  `collectibleProductId`, `serialNumber` (nulo); `nextPageCursor`.
- `GET …/item/{cid}/resale-data` → `recentAveragePrice`, `priceDataPoints[]`, `volumeDataPoints[]` (lido com sessão).
- `GET …/item/{cid}/get-resale-parameters` → `minimumFee`, `priceFloor`, `resalePercentageFee`,
  `resellableLimitedItemPriceFloors` (**ainda não usado**: deve virar o piso mínimo do Roblox e a taxa real).
- **`PATCH …/item/{cid}/instance/{instanceId}/resale` → `updateSaleResult`** — é o "vender / mudar preço" da página.
  Apareceu 3× com HTTP 200, sem pedir 2FA. **O app não usa.** O corpo (preço, etc.) não foi gravado (a gravação não
  guarda corpos de requisição); confirmar antes de usar.
- Clássicos (economy.roblox.com `resellable-copies`) responderam 404; o resale-data clássico ficou congelado na migração.

## Regras do usuário que valem para qualquer automação
- Não modificar a autenticação existente das contas; não expor cookies, tickets ou tokens em logs; não copiar
  credenciais para o Roblox Player; preservar o isolamento das sessões.
- Não executar transferências de Robux automaticamente. Trades nunca são enviadas/aceitas pelo app.
- Nunca contornar captcha/2FA: se o Roblox pedir verificação, parar e avisar.
- Dados fictícios só em testes.

## Risco
Mudar preços sozinho, repetidamente, é automação de mercado; os termos do Roblox restringem uso automatizado e contas
com robôs já foram moderadas. Por isso: etapas, e o modo totalmente automático só com aceite explícito do usuário.

## Onde se perde Robux (e a regra que evita)
| Perda | Regra |
|---|---|
| Guerra de preço (cortes de 1 em 1; cada 1 Robux = 0,70 líquido) | esperar 20–30 min superado antes de reagir; máximo de cortes por cópia/dia; queda máxima/dia; detector de guerra (concorrente reage em minutos → parar e avisar) |
| Seguir "dump" (cópia muito abaixo do 2º menor / do RAP) | ignorar outliers; esperar a cópia barata vender |
| Concorrer consigo mesmo | só a cópia mais barata entre as alts disputa; as outras escalonadas acima (prioridade: conta com cota) |
| Pressa sem motivo | conta sem cota não tem pressa; decidir pela fila: espera ≈ posição ÷ vendas/dia |
| Deixar dinheiro na mesa | em 1º com folga, subir até logo abaixo do próximo |
| Abaixo do mínimo | piso = maior entre `priceFloor` do Roblox, X % do RAP/valor Rolimons e o mínimo por item |
Sempre: reler depois de cada mudança; parar em verificação, 429, recusa ou qualquer coisa fora do esperado.

## Etapas combinadas
1. **Histórico**: guardar cada leitura (meus preços, concorrentes, vendas com data/valor) — base para ajustar regras.
2. **Modo sombra** (1–2 semanas): mostrar o que o robô faria ("baixaria X de 3.590 para 2.999, −413 líquido") sem fazer.
3. **Um clique**: "Aplicar sugestões (N)" com preço antigo → novo e impacto no líquido; confirmar; aplicar via PATCH; reler.
4. **Automático com travas** (só se o usuário aceitar o risco): por item/conta, todas as regras acima, botão de parar
   tudo, resumo diário.
5. **Modelo** (depois, com histórico): probabilidade de vender em X dias por preço → preço de maior líquido esperado.

## Perguntas em aberto (o usuário ainda não respondeu)
1. **Piso**: geral (ex.: 80 % do RAP), por item, ou os dois?
2. **Espera aceitável**: quantos dias esperar uma venda antes de valer cortar (padrão ou por item)?
3. **Risco**: aceita o modo totalmente automático no futuro, ou parar no "um clique"?

## Como trabalhar no projeto
- .NET 8; `src/RbxSwitcher.Core` (net8.0, testável) e `src/RbxSwitcher.App` (WinForms, código sem designer, compila no
  Linux com `EnableWindowsTargeting`). Testes: `dotnet test tests/RbxSwitcher.Core.Tests` (293 passando em 0.7.5).
- Prints sem Windows: `tools/UiPreview` (Wine; ver o README dela). Teste dos scripts de página: `tools/TradeAssistCheck`.
- Publicar: `dotnet publish src/RbxSwitcher.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
  -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none`.
- Versão em `Directory.Build.props`; histórico de mudanças no README ("Estado").
- O usuário escreve em português; respostas e textos do app em português; datas no horário de Brasília.
