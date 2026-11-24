# Aba Limiteds — controle de venda (0.7.0)

Todos os Limiteds de todas as contas numa lista só, com a situação de venda de cada cópia. **Só leitura**: o app nunca
anuncia, muda preço ou tira da venda. "Vender" / "Mudar preço" abrem a página do item na aba da conta certa; você
confirma no Roblox.

## Quais contas aparecem
- O app lê os Limiteds de **todas as contas com sessão** (as que não tinham nenhum, a cada 2 h).
- Aparecem as contas com algum Limited (em holding, pronto ou à venda) ou com venda nos últimos 7 dias. As outras viram
  só um número no rodapé ("7 contas sem Limiteds para vender").
- Contas de origem (FonteAlfa, Beta) entram, marcadas "origem". Cópia que está numa etapa da Distribuir aparece
  "no plano E004".
- Smalls ficam ocultos por padrão (chip "Mostrar Smalls").

## Cada cópia
| Situação | Quando |
|---|---|
| **Superada · 4º** | à venda, e outro vendedor pede o mesmo ou menos (o 4º é a sua posição entre os 10 menores) |
| **À venda · 1º** | à venda e a mais barata |
| **Pronta para vender** | liberada e fora da venda |
| **Livre · não lido** | liberada; o app ainda não leu se está à venda |
| **Em holding** | ainda não pode ser vendida ("chegou dd/mm" quando veio por uma etapa) |
| **Sem Plus** | a conta não tem Plus ativo: o Roblox não deixa vender |
| **Vendida** | venda dos últimos 7 dias (creditada ou pendente) |

Colunas: meu preço, menor preço de **outro** vendedor (se for outra conta sua: "sua @conta"), RAP · vendas por dia,
líquido (do seu preço; "≈" = pela sugestão).

Cabeçalho de cada conta: cota de 30 dias, quanto libera e quando (quando está zerada), Plus. Sem cota, o detalhe avisa:
"o Robux da venda fica parado até dd/mm" — é aviso, não bloqueio.

## Sugestão de preço
1 Robux abaixo do menor preço de outro vendedor (configurável), **nunca abaixo do piso** (80 % do RAP por padrão). Se
o menor já está abaixo do piso, a sugestão é o piso, com o aviso "anunciar no piso ou esperar". Sem ninguém vendendo:
o valor da Rolimons ou o RAP. "Copiar" (e "Vender"/"Mudar preço") põem o número na área de transferência.
Configurar…: "Venda: abaixo do menor preço" e "Venda: piso da sugestão (% do RAP)".

## Leituras
Ordem de cada passada (o item lido há mais tempo vem primeiro, então uma passada interrompida continua de onde parou):
1. Limiteds de cada conta (quando vencidos).
2. **Id de cada item no Marketplace**, em lote (até 50 itens por pedido ao catálogo). Os ids ficam guardados no cache de
   mercado: depois da primeira vez, não há busca.
3. **Menores preços** de cada item (até 100), com a sessão de uma conta que tem o item. Ficam guardados no vault
   (criptografado, porque mostram os anúncios das suas contas) e valem depois de reabrir o app.
4. **Anúncios de cada conta** (quais cópias estão à venda), com a sessão da própria conta.
5. Vendas (últimos 31 dias) e vendas por dia.

- Um item só é lido quando o id dele no Marketplace é conhecido. Os endereços antigos do Roblox (economy) respondem 404
  ou números congelados na migração ("0/dia") e só são usados para um item que o catálogo diz não ter id no Marketplace.
- Uma falha vale **só para o item que falhou**: nunca para os outros itens nem para as outras contas. (Até a 0.8.1, uma
  única resposta 404 — ou três falhas seguidas — parava a leitura dos anúncios de todas as contas até fechar o app:
  era daí que vinham tantos "Livre · não lido".)
- Quando o Roblox limita os pedidos (HTTP 429), a leitura espera a pausa e continua do mesmo item, em vez de ser
  abandonada.
- Uma leitura dos anúncios mais antiga que os menores preços continua valendo quando os menores preços não dizem nada
  sobre a cópia (ela não está entre eles e eles não são todos os anúncios do item). "Livre · não lido" fica só para o
  item que nunca foi lido.
- Automático a cada 15 min enquanto há algo pronto ou à venda (nunca junto com a leitura de saldos); ao abrir a aba, se
  a última leitura tem mais de 10 min. "Atualizar leituras" relê tudo; "Reler" relê só a cópia escolhida.

## Saída do holding
Ao clicar numa cópia **Em holding**, o painel mostra quando ela sai do holding (horário de Brasília), quanto falta e de
onde veio a data. A regra é a do Roblox: **2 dias exatos depois de receber por trade** e **7 dias exatos depois de
comprar com Robux na loja**.

De onde o app tira a chegada de cada cópia em holding:
- **Compras na loja:** o registro de compras da conta (`transaction-records`, tipo Purchase, últimos 9 dias) dá a hora
  exata.
- **Trades:** as trades concluídas da conta (últimos 3 dias) dizem quais itens chegaram por trade. Cada trade é lida uma
  vez só (trade concluída não muda).
  - Se a trade foi **aceita no app** (em qualquer aba de conta), o app anota a hora em que você aceitou: é a hora exata.
  - Se foi aceita fora do app, o Roblox só informa quando a trade foi criada. A saída fica entre "criação + 2 dias" e
    "quando o app viu a cópia + 2 dias", e o painel mostra as duas pontas.
- **O que o app viu:** a cada leitura do inventário, o app anota quando viu cada cópia pela primeira vez. Sem trade nem
  compra que explique a cópia, o painel mostra as duas datas possíveis (se veio por trade, se foi comprada).
- Cada cópia é ligada a uma chegada só: pelo id da cópia, depois pela série, depois pela chegada mais recente daquele
  item antes de o app ver a cópia.

As compras e trades são lidas só nas contas com cópias em holding: na leitura da aba (a cada 20 min enquanto faltar
explicar alguma cópia, a cada 6 h quando todas já estão explicadas) e no "Reler" de uma cópia em holding. Ficam no vault
por 9 dias. Se a data passar e o Roblox ainda mostrar a cópia em holding, o painel avisa.

## Pendente de confirmação no Windows
As leituras de anúncio usam os endpoints conhecidos — `economy.roblox.com/v1/assets/{id}/resellers` e
`…/users/{userId}/resellable-copies` para Limiteds clássicos; `apis.roblox.com/marketplace-sales/v1/item/{id}/resellers`
e `…/resellable-instances/{userId}` para itens do Marketplace novo (o app descobre qual usar pelo `collectibleItemId`
do item e tenta o outro se um falhar). **Não foram vistos com contas reais.** Se as situações vierem erradas ou como
"Livre · não lido": escolha uma cópia, clique **Gravar venda**, abra Vender na página do item (pode cancelar) e envie o
arquivo `venda-chamadas.txt` da pasta de dados — ele tem só caminhos e nomes de campos, sem valores.

## 0.7.1 — o que os dados reais mostraram
- A lista de **menores preços funciona** (o detalhe mostrou "3º · você (@conta) 1.825").
- A leitura das **cópias à venda de cada conta respondeu 404**. Agora o app tira "à venda", "superada" e o seu preço da
  própria lista de vendedores (a conta aparece ali como vendedora), e não repete a leitura com 404 naquela sessão.
- Limite: só os **10 menores preços** de cada item são lidos. Se a sua cópia estiver à venda acima deles, ela aparece
  "Livre · não lido". Com menos de 10 vendedores e a conta fora da lista, a cópia está fora da venda ("Pronta").
- Para trocar pela leitura direta, ainda vale gravar a página de venda ("Gravar venda") e enviar `venda-chamadas.txt`.

## 0.7.3 — endpoints confirmados pela gravação (venda-chamadas.txt, 19/11)
A página do item no Roblox chama, para itens do Marketplace (todos os do teste tinham `collectibleItemId`):
- `GET apis.roblox.com/marketplace-sales/v1/item/{collectibleItemId}/resellable-instances?ownerType=User&ownerId={id}&limit=…`
  → `itemInstances[]` com `price`, `saleState`, `isHeld` e `serialNumber` **nulo**. O app usava `…/resellable-instances/{id}`
  (não existe: era o 404). Como não vem id nem série, as cópias à venda são ligadas às cópias liberadas da conta em ordem
  (cópias do mesmo item são equivalentes aqui).
- `GET …/item/{collectibleItemId}/resellers?limit=100` → os 100 menores, com `nextPageCursor` (vazio = lista completa).
- `GET …/item/{collectibleItemId}/resale-data` → RAP e volume diário: as vendas por dia dos itens migrados vêm daqui
  (o endpoint clássico parou de contar, por isso aparecia "0/dia").
- `PATCH …/item/{id}/instance/{id}/resale` é o "mudar preço/vender" da página. **O app não usa**: continua só lendo.

## 0.7.4 — vendas por dia e RAP
- O "0/dia" e o RAP estranho (Blue 8-Bit Antlers 1.858 com o menor preço em 3.647) vinham do `resale-data` **clássico**,
  que parou na migração do item para o Marketplace. Itens com `collectibleItemId` agora usam só o `resale-data` do
  Marketplace, lido **com sessão** (uma conta que tem o item), como a página do Roblox. Se falhar: "—", nunca "0".
- RAP: o da **Rolimons** tem prioridade; o do Roblox só preenche itens que a Rolimons não acompanha.
- A API pública da Rolimons não tem contagem de vendas (só RAP, valor, **demanda** e tendência). Quando o Roblox não
  dá a contagem, a coluna mostra "demanda X/4" da Rolimons. As vendas que a Rolimons registra ficam só na página de
  cada item do site; ler essa página é possível, mas não dá para testar daqui.
