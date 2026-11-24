# TradeAssistCheck — testa a pré-seleção da trade sem o Roblox

Ferramenta de desenvolvimento (não vai no app). Roda `src/RbxSwitcher.App/Browser/TradeAssist.js` num Chromium contra
`mock.html`, uma página de trade **fictícia** (não é a do Roblox): dois inventários com abas ou lado a lado, 4 itens por
página, séries, o mesmo item dos dois lados, uma imagem dentro de link e um botão "Make Offer" que nunca pode ser clicado.

```bash
cd tools/TradeAssistCheck && node run.js ../../src/RbxSwitcher.App/Browser/TradeAssist.js
```

Esperado (cerca de 1 min e meio no total), com `sent:false` e o endereço da página sem mudar em todos:
1. `tabs`: os 4 itens selecionados (troca de aba e de página; imagem dentro de link não navega).
2. `both`: Epic Egg #2 na oferta e #7 no pedido; "Ghost Item" em `missing`.
3. `both`: Sword #3 não é clicado (o plano pede #99).
4. `tabs`: as duas "Twin Cap" sem série (a cópia que vai para a oferta não é clicada de novo), "Counterfeit Hat"
   (nome com palavra proibida para botões) e Rock #1 pela aba do parceiro — sem seguir o link do perfil no título.
5. `real` (`mock-real.html`, cópia da página de 19/11/2026): etapa E002 inteira — Black Headphones e uma das duas
   Eggcient Woolly Mammoth na oferta, Deteggctive W. Wolf **do inventário de sigma2** (título com o nome de exibição)
   no pedido, mesmo existindo duas no seu inventário; nada em "Sua oferta"/"Seu pedido" é clicado.
6. `real`: item na página 2 do seu inventário e Verdant Crown #1117834 do parceiro.
7. `slow`: aba recém-aberta — o inventário do parceiro só aparece 4 s depois; os 3 itens da E002 são marcados.
8. `mousedown`: página que só seleciona no mousedown (o script repete o clique com a sequência de mouse).

A página real do Roblox pode ter outra estrutura. Quando a pré-seleção não acha um item, o app salva
`trade-pagina.txt` na pasta de dados (estrutura sem valores nem nomes de conta) para ajustar o script.

## Abrir a trade na destinatária

```bash
node run-open.js ../../src/RbxSwitcher.App/Browser/TradeAssist.js
```

`mock-trades.html` é uma lista de Trocas fictícia que abre em "Enviadas", com botões Aceitar/Recusar dentro de cada linha e
um link de bate-papo com o mesmo nome. Esperado: `rows` troca para "Recebidas" e abre a linha mais nova de Beta
(`row:Beta:0`), com `accepted:false` e `declined:false`; `link` devolve o link direto da trade (`navigate`).
