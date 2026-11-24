# Calendário de Robux

A aba **Calendário** mostra quando os Robux das vendas de Limiteds devem cair em cada conta. Depois de uma venda, o
Roblox deixa o valor como **pendente** por um tempo e só depois credita no saldo.

## O problema: o Roblox não informa a data
O Roblox não diz quando cada venda pendente cai. A central de ajuda fala em "até 30 dias", e na prática o prazo varia
por conta (relatos de 3 a 14 dias). O registro de vendas (`transaction-records/v1/users/{id}/transactions?transactionType=Sale`)
só diz se cada venda ainda está pendente (`isPending`). Por isso as datas do calendário são **estimativas**, e a
tela sempre diz de onde cada uma veio.

## Como o app aprende o prazo
- As vendas de cada conta são lidas dos **últimos 31 dias**, para que toda venda ainda pendente esteja na lista.
- Cada venda que estava pendente numa leitura e aparece **creditada** na seguinte vira um registro: ela caiu entre
  essas duas leituras. Com leituras a cada ~25 minutos, o momento fica bem conhecido. O app guarda os últimos 150
  registros de cada conta, por até 120 dias, na vault criptografada.
- O prazo de uma conta vem, nesta ordem:
  1. do **histórico da própria conta** (2 ou mais vendas vistas caindo). O app usa o valor do meio para cima, porque
     um calendário um pouco atrasado é mais seguro que um adiantado;
  2. do que as **vendas lidas da conta** mostram: se toda venda pendente é mais nova que toda venda creditada, o prazo
     é maior que a pendente mais velha e no máximo a creditada mais nova (o prazo das outras contas é ajustado a essa
     faixa);
  3. do **histórico de todas as contas**;
  4. sem nada disso, **7 dias**, até o app ver vendas caindo.
- Uma venda que passou da estimativa e continua pendente fica no dia de **hoje**, em amarelo: "pode cair a qualquer
  momento", com o limite de 30 dias do Roblox.

## A tela
- **Indicadores** (no topo): pendentes de vendas (vendas e contas), **hoje** (com as atrasadas), **próximos 7 dias** e
  **próxima queda** (dia, valor e conta). "Hoje" e "Próxima queda" levam ao dia no calendário.
- **Como é estimado:** uma linha com o prazo usado e quando as vendas foram lidas; o botão abre a explicação inteira.
- **Calendário do mês:** só as semanas do mês. Dia com Robux a cair: fundo verde suave, o valor e uma barra do tamanho
  do valor (comparado ao maior dia na tela). Venda que passou da estimativa: âmbar, com o ponto e a palavra "atrasada".
  "caiu" mostra o que o app viu cair. Hoje tem contorno azul; o dia escolhido, um anel (branco quando o calendário tem o
  foco do teclado). Passar o mouse mostra o dia por conta. Teclado: setas, Home (hoje), Page Up/Page Down (mês). Em
  telas pequenas ou com escala alta, as células encolhem: o dia e o valor ficam na mesma linha e o valor abrevia
  ("2,1k"); nada é cortado. A legenda fica embaixo.
- **Conta:** mostra só as vendas de uma conta (indicadores, calendário e dia). Clicar na conta em "Por conta" faz o
  mesmo; clicar de novo volta para todas.
- **Dia escolhido** (painel à direita, como o de detalhes da aba Limiteds): cada venda com a imagem do item, a conta, a
  data da venda, o valor à direita e a situação (≈ data prevista e faixa; atrasada; ou entre quais leituras caiu).
- **Por conta:** cada conta com os pendentes de vendas à direita, a próxima queda e o prazo; avisos curtos (pendentes
  sem data, vendas não lidas). A explicação completa fica na dica ao passar o mouse.
- **Primeiro uso:** sem vendas lidas, a tela diz para usar "Atualizar vendas" (e mostra "lendo" enquanto lê).

## Leituras
- **Atualizar vendas** lê as vendas de todas as contas com Limiteds, com Robux pendentes ou já lidas antes, uma por
  vez, respeitando a pausa quando o Roblox limita os pedidos (HTTP 429).
- Ao abrir a aba, as contas com vendas lidas há mais de 25 minutos são relidas em segundo plano. O ciclo automático de
  saldos também relê, a cada 25 minutos, as vendas das contas com Robux pendentes.
- Só leitura: o app não move Robux nem muda nada nas contas.

## Pendente de confirmação no Windows
- Confirmar que uma venda creditada continua na lista de transações com `isPending: false` (é assim que o app vê a
  queda). Se a venda sumir da lista ao ser creditada, o histórico não se forma e o prazo fica no padrão.
