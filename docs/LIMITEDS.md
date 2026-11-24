# Distribuição de Limiteds (substituto do Otimizador do Limited Desk)

Objetivo: distribuir os Limiteds que chegam pelos saques da AstraFlip entre as contas para colocá-los à venda cedo,
sem que as destinatárias acumulem estoque ou Robux acima do que conseguem escoar pela cota de envio do Plus.
A troca de contas continua sendo a função principal do app: a distribuição fica na aba Plus e nas Configurações.

## Etapas

| Etapa | O que faz | Versão |
|---|---|---|
| 1. Leituras e espaço | Limiteds de cada conta, holding, valores (RAP, Rolimons), liquidez, configuração, espaço por conta | 0.5.0 |
| 2. Sugestão | Qual Limited mandar, de qual origem, para qual conta, com quais Smalls e por quê; o que ficou de fora; **Abrir trade**; preço manual por item e meta por conta | 0.6.0 |
| 3. Conferir e replanejar | Trade pelo ID ou localizada no Roblox; os dois lados conferidos; Small substituto aceito; inventários ajustados; etapas futuras refeitas; etapas feitas por fora em conferência; histórico | 0.6.0 |
| 4. Venda | Vendas pelo Transactions ligadas à cópia só quando é seguro; "a conferir" no resto | 0.6.0 |

O envio da trade é sempre feito por você no Roblox: o app abre a página certa e depois lê o Roblox para conferir.
Automatizar o envio (robô de trade) arrisca banimento e exigiria contornar verificações (captcha/2FA), o que o
projeto não faz. Leituras, replanejamento, conferência e avisos são automáticos.

## Configuração (Configurações → Distribuição de Limiteds)

| Campo | Padrão | Uso |
|---|---|---|
| Contas de origem | — | Onde caem os saques da AstraFlip (Alfa, Beta). Todo Limited que não é Small nelas veio da AstraFlip |
| Destinatárias | todas as outras | Um grupo do app, ou todas as contas que não são origem |
| Taxa do Marketplace | 30 % | Líquido = preço × (1 − taxa), arredondado para baixo. 3.000 → 2.100 |
| Meta líquida por conta | 0 (sem teto) | Teto de Robux líquidos parados em cada destinatária |
| Limite de RAP para Small | 1.000 | Cópia com RAP ≤ limite é Small (item de apoio). O RAP decide a classe, não o preço de venda |
| Reserva mínima de Smalls | 1 | Smalls que cada conta guarda (usado na etapa 2) |
| Dias até a venda | 7 | Do envio até o Robux da venda chegar; usado no espaço "na venda" |
| Usar valor da Rolimons | não | Sem isso, o preço estimado é o RAP |
| Preço manual por item | — | Aba Distribuir → Ficaram de fora → **Preço…** (vale para todas as cópias do item) |
| Meta por conta | padrão | Aba Plus → detalhes da conta → **Meta desta conta…** (vazio = padrão; 0 = sem teto) |
| Planejar pelo espaço na venda | sim | Usa o espaço que a conta terá quando o Robux da venda chegar; desligado, usa só o de agora |

## Cálculo

```
líquido da cópia = floor(preço × (100 − taxa) / 100)        preço: manual > valor Rolimons (se ligado) > RAP
ocupado          = saldo + pendentes + estoque líquido (+ entradas comprometidas, etapa 2)
espaço agora     = max(0, min(meta líquida, cota restante de 30 dias) − ocupado)
espaço na venda  = max(0, min(meta líquida, cota restante + o que a janela de 30 dias libera até a venda) − ocupado)
```

- Estoque conta cada cópia **uma vez** (pelo `userAssetId`) e **não inclui Smalls**. Cópias sem id ficam "a conferir"
  e não entram em nenhum total.
- Exemplo do Desk: cota restante 8.000, ocupado 3.500 → 4.500 (com meta ≥ 8.000). Com meta 6.000 → 2.500.
- **Espaço na venda** é a melhoria sobre o Desk: o Robux de uma venda chega dias depois, e até lá a janela de
  30 dias já liberou parte da cota (o app já calcula essas liberações).
- **Bloqueia novos lotes** (coluna "bloqueada", com o motivo na dica): sem Plus ativo confirmado, cota de 30 dias não lida
  ou esgotada, saldo/pendentes não lidos, inventário não lido por completo, cópia sem preço estimado.

## Na tela

- **Plus → coluna Espaço:** espaço agora de cada destinatária, "origem" nas contas de origem, "bloqueada" (dica com o
  motivo) ou "—" fora da distribuição. Ordenável.
- **Detalhes da conta → cartão "Limiteds e espaço":** papel da conta, aviso de bloqueio, Espaço agora / Na venda / Ocupado,
  contagem (Limiteds, em holding, Smalls, a conferir), os 8 de maior líquido (com origem do preço, vendas por dia ou
  demanda da Rolimons, holding), os Smalls e **Ler Limiteds agora**. Nas contas de origem: "Prontos para distribuir:
  N · líquido X".

## Aba Distribuir

**Sugestões (etapa 2).** "Gerar sugestões" monta o plano com as leituras atuais:

1. Cópias das origens que não são Small, têm preço e não estão em outra etapa. Ordem: primeiro as liberadas, depois
   as em holding; em cada grupo, liquidez (vendas/dia do Roblox; ≥ 1/dia empata; sem isso, a demanda da Rolimons),
   depois o maior líquido. Uma cópia em holding entra no plano numa etapa **"Aguardando liberação"** (o Roblox não deixa
   colocá-la numa trade antes): a etapa já reserva o espaço e o Small da destinatária, o "Abrir trade" fica desligado
   até a cópia sair do holding e ela só se junta a outras cópias que também esperam (não atrasa uma trade que pode ir
   agora). Quando a releitura mostra a cópia liberada, a etapa vira uma sugestão comum.
2. Para cada cópia: se já há uma trade sugerida da mesma origem para uma destinatária que ainda tem espaço, ela entra
   nessa trade (até **3 Limiteds**). Senão, abre uma trade nova com a destinatária de **maior espaço livre** que tenha um
   Small para mandar.
3. Smalls: a destinatária manda 1 (de preferência um livre; se só houver em holding, a etapa fica "Aguardando Small");
   a origem manda 1 só se tiver além da **reserva** guardada para os saques da AstraFlip (a reserva conta só Smalls liberados: um em holding não serve para um saque). Cada lado fica com o Small que
   recebe e pode usá-lo depois do holding.
4. Cada etapa mostra o porquê (destino, liquidez, líquido, Smalls, espaço antes → depois) e a conta do espaço da
   destinatária em três linhas: *Espaço* (teto − ocupado = espaço, com o espaço de agora ao lado do "na venda"),
   *Ocupado* (saldo + pendentes + Limiteds parados na conta + lotes a caminho) e *Teto* (cota de 30 dias que sobra +
   o que a janela libera até a venda, limitado pela meta) e cada cópia fora do plano
   mostra o motivo (sem preço, sem espaço — com o maior espaço livre —, destinatárias bloqueadas, sem Small,
   origem sem Plus).

**Abrir trade.** Antes de abrir, o app reconfere a etapa: leituras com até 30 min (Limiteds das duas contas e o saldo
da destinatária, de onde sai o espaço), Plus nas duas contas, as cópias e os Smalls onde deveriam estar e livres, e o
espaço da destinatária. Se algo estiver velho, relê as duas contas na hora; se
ainda assim não fechar, recalcula as sugestões e explica. Pronta: a etapa fica "Em montagem", o app abre a aba da conta
de origem na página de trade com a destinatária e a barra de status diz o que mandar e o que pedir.

**Pré-seleção dos itens** (ligada por padrão; desliga em Configurar…). Com a página de trade aberta na aba da origem, o
app roda um script nessa página que marca os Limiteds e o Small da origem (oferta) e o Small da destinatária (pedido),
achando cada um pelo nome e pela série (troca a aba de inventário e passa as páginas se precisar). Regras:
- só clica em cartões de item, abas de inventário e setas de página; **nunca** em enviar, aceitar, contraproposta,
  recusar ou cancelar — quem envia é você, depois de conferir;
- cada lado é achado pelos títulos da página: "Seu inventário" (oferta), "Inventário de <nome de exibição>" (pedido);
  cartões em "Sua oferta" e "Seu pedido" nunca são clicados (seria tirar o item da trade);
- um cartão com outra série não é marcado; cópias iguais sem série visível são equivalentes e a primeira livre é
  marcada (a conferência aceita outra cópia do mesmo item quando o Roblox informa qual foi, e o plano passa a seguir
  a cópia real);
- não lê cookies nem armazenamento da página; leva só nomes e séries dos itens e os nomes das duas contas;
- a barra de status diz quantos itens foram marcados e quais faltam, e a etapa guarda isso no histórico;
- se algo não foi achado, o app salva `trade-pagina.txt` na pasta de dados: a estrutura da página (tags e classes),
  com números trocados por 9 e nomes de conta por "@conta". Enviar esse arquivo permite ajustar o script à página
  real do Roblox (a pré-seleção foi testada só contra uma página fictícia, em `tools/TradeAssistCheck`).

**Depois de enviar** (ligado por padrão; desliga em Configurar…). O app percebe, na aba da origem, quando você envia a
trade no site (depois da verificação em duas etapas, se o Roblox pedir): é a resposta do próprio Roblox ao envio, de onde
só o ID da trade é lido. Então:
1. confere essa trade com a etapa (fica "Enviada"); com mais de uma etapa aberta para a mesma destinatária, a trade vai
   para a etapa com que ela bate;
2. se bate, troca para a conta destinatária, abre Trocas → Recebidas e abre a trade que veio da origem — clicando no
   nome da origem ou no link da trade, nunca em Aceitar/Recusar/Contraproposta. **Aceitar é com você**;
3. se **não** bate (item a mais, Robux, outra cópia sem id), o app não troca de conta e avisa para não aceitar;
4. quando você aceita na destinatária, o app percebe, confere de novo (algumas vezes, enquanto o Roblox conclui) e a
   etapa fica "Concluída", com as sugestões seguintes recalculadas. Se o envio não foi visto (feito fora do app ou sem
   ID na resposta), a trade aceita é ligada à etapa aberta dessa destinatária com que ela bate.
Se a página de Trocas tiver outra estrutura e a trade não abrir, o app salva `trocas-pagina.txt` na pasta de dados.

**Conferir (etapa 3).**
- *Localizar trade no Roblox:* procura nas trades da origem (enviadas, concluídas, inativas, recebidas) uma com a
  destinatária feita depois da etapa e confere — nunca uma trade já ligada a outra etapa (a trade concluída de uma
  etapa anterior entre as mesmas contas não é confundida). *Informar ID…* aceita o número ou o endereço da trade e
  avisa se o ID já é de outra etapa. Um ID de outra conta é recusado sem desligar a trade que a etapa já tinha.
- Conferência: as duas contas são as da etapa; cada Limited planejado está no lado da origem (por `userAssetId`; sem ele,
  por item + serial, e uma série diferente nunca passa como a cópia planejada; **outra cópia do mesmo item** com id
  informado pelo Roblox é aceita e registrada — "outra cópia de X: o plano previa #A, foi #B"; cópias iguais sem id
  (trades v2, ex.: 2 Molten Lava Wings) batem quando são tantas quanto as do plano; uma cópia a mais fica "a conferir").
  Os Smalls planejados também são achados por item + serial quando o Roblox não dá o id (trades v2). Outro Small no
  lugar do planejado é aceito quando é Small e tem id (fica registrado: "o plano previa A, a trade levou B"); item a mais que não é Small, item faltando ou
  Robux na trade deixam a etapa **Em conferência**.
- Resultado: aberta no Roblox → "Enviada" (o app confere de novo sozinho a cada ciclo); concluída → "Concluída", e as
  cópias reais passam de uma conta para a outra no app (em holding) até a próxima leitura; recusada, expirada ou
  contraproposta → as cópias voltam para o plano.
- Replanejamento: depois de cada trade concluída e de cada leitura de inventário, as sugestões são refeitas com o
  estoque novo (o caso FonteAlfa → sigma2: o Small real recebido passa a ser o usado na etapa seguinte Beta → sigma2,
  esperando a liberação). Etapas abertas não são refeitas: se a composição delas deixa de valer, ficam em conferência.
- Se as cópias de uma etapa aparecem na destinatária sem trade conferida, a etapa fica em conferência: "a trade pode
  ter sido feita fora do RbxSwitcher".
- Limiteds que só a Rolimons lista (UGC novos, fora da lista de colecionáveis do Roblox) podem demorar horas para mudar
  de conta na Rolimons depois de uma trade. O plano corrige isso sem mexer nas leituras salvas: uma cópia que saiu numa
  trade concluída não é sugerida de novo enquanto a Rolimons ainda a mostrar na origem (até 2 dias), uma cópia que
  chegou conta no estoque da destinatária (em holding) por 1 dia mesmo antes de aparecer, e as cópias da conta no
  Marketplace (lidas pela aba Limiteds depois do inventário) limitam quantas cópias só da Rolimons são reais. A lista do
  próprio Roblox nunca é alterada. O espaço da aba Plus usa a mesma conta.
- Histórico: cada etapa guarda o que aconteceu, com data (sugerida, aberta, trade conferida, Small trocado, recusada…).

**Vendas (etapa 4).** As vendas das destinatárias que receberam cópias são lidas desde a primeira entrega (a paginação segue pelas datas de
  todas as vendas, então vendas de game pass não escondem as de itens). Uma venda é
ligada à cópia só quando é seguro: a única cópia do plano daquele item na conta, ou — havendo várias — a única que saiu
do inventário depois de uma leitura completa. O resto fica **a conferir**. Cada cópia entregue mostra: em holding, pode
ser anunciada, vendida (valor creditado, pendente ou não, e diferença para o líquido estimado) ou saiu sem venda
ligada (a conferir). Vendas de itens fora do plano são contadas à parte.

## APIs

| Para quê | Endpoint | Sessão |
|---|---|---|
| Limiteds da conta (`userAssetId`, serial, RAP, `isOnHold`) | `inventory.roblox.com/v1/users/{id}/assets/collectibles` (todas as páginas) | da própria conta, depois de confirmar a identidade |
| RAP e vendas/dia (últimos 30 dias) | `economy.roblox.com/v1/assets/{id}/resale-data` | nenhuma |
| Valor, RAP e demanda de todos os itens | `api.rolimons.com/items/v2/itemdetails` (1 pedido a cada 30 min) | nenhuma |
| Holdings (quando o Roblox não diz) | `api.rolimons.com/players/v1/playerassets/{id}` | nenhuma |
| Uma trade (conferência) | `trades.roblox.com/v1/trades/{id}` (v2 como reserva) | da origem; se não achar, da destinatária |
| Trades recentes (localizar) | `trades.roblox.com/v1/trades/{Outbound,Completed,Inactive,Inbound}` | da origem |
| Vendas | `apis.roblox.com/transaction-records/v1/users/{id}/transactions?transactionType=Sale` | da destinatária |

- A Rolimons usa uma conexão separada que só aceita `https://api.rolimons.com` e nunca leva cookie.
- Leituras de Limiteds: só das contas de origem e destinatárias, junto com os saldos, a cada 25 min, uma conta por
  vez, respeitando a pausa do 429. Liquidez: até 20 itens por passada, a cada 6 h por item; um item cuja leitura falhou espera 30 min e vai para o fim
  da fila, sem travar os outros.
- Dados públicos de mercado ficam em `market-cache.json`; os Limiteds de cada conta ficam no cofre criptografado.

## Pendente de confirmação no Windows

- Formato real das respostas (os testes usam respostas fictícias). Se a leitura falhar, o motivo aparece no cartão e o
  log registra os nomes dos campos.
- Se `isOnHold` vem preenchido no inventário; se não, a lista de holds da Rolimons é usada.
- Formato real das trades (v1 com `userAssets`, ou v2 com `participantAOffer/participantBOffer`) e das vendas.
- Fim exato do holding (a lista de negociáveis da API nova de trades): hoje o app só sabe "em holding" ou não.
