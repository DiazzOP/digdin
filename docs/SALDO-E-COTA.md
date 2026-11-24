# Saldo, Robux pendentes e cota de transferência

O RbxSwitcher é um **alternador de contas**. Este módulo só **lê, calcula e mostra** o
saldo da conta ativa. Inventário, trades, distribuição de Limiteds e lotes ficam no Limited
Desk; a lógica de leitura e cálculo segue a dele como referência.

## Na tela

No topo, um controle segmentado **Contas | Plus** troca a vista. O visual segue o design system do Bloxkeep (ver `THIRD-PARTY-NOTICES.md`): status sempre como **ponto colorido + palavra**, botão principal claro (um por área), verde só para "sessão ativa", menus com o item destrutivo por último em vermelho.

- **Contas** (padrão): o navegador fica à direita. À esquerda, no topo, o cartão da conta ativa com **Saldo**, **Pendentes**, **Pode enviar agora**, **Restante hoje**, **Restante 30 dias** e o chip **Plus · status**. Logo abaixo ficam a busca, o seletor de grupo e a lista compacta (avatar com ponto de sessão, nome, @usuário, saldo, status da sessão e do Plus; a conta aberta tem um trilho claro à esquerda). Clicar no chip do Plus abre a aba Plus nos detalhes dessa conta.
- **Plus:** uma tabela com **todas as contas** (conta, status do Plus, renovação ou validade, cota de hoje, cota de 30 dias), com busca, seletor de **grupo** e chips de **status** com a contagem de cada um (Todos, Ativo, Expirando, Em teste, Sem Plus, Expirado, A conferir).
  - **Ver detalhes** ou um duplo clique abre um **painel lateral** com quatro cartões: **Assinatura** (status, validade e renovação, avisos, *Atualizar dados*), **Cotas de transferência** (três números lado a lado: Hoje, 30 dias e Por transferência, com o teto e quando renova ou libera), **Histórico de 30 dias** (*Reler histórico completo*) e **Diagnóstico** (em *Mostrar ferramentas*: diagnóstico da leitura e gravação da página do Plus).
  - **Atualizar tudo** consulta todas as contas na hora; `F5` atualiza a conta selecionada.

**Limite reduzido (aviso):** a cada leitura bem-sucedida dos tetos e da restrição, o app compara o limite efetivo
diário e de 30 dias (`min(teto do Roblox, sua restrição)`) com a leitura anterior. Se algum **diminuiu** (ex.: de
5.000/10.000 para 500/1.000 quando o Plus vence), aparece um aviso amarelo no cartão da conta em Contas e nos detalhes da
aba Plus, por exemplo "Teto diário caiu de 5.000 para 500 (Roblox); teto de 30 dias caiu de 10.000 para 1.000 (Roblox).
Detectado em 17/11 14:20." A origem é **Roblox** (teto da conta) ou **sua restrição nas configurações**. Na lista do
Plus, a cota afetada aparece com **↓** em amarelo e há o filtro **Limite reduzido**. O aviso some com **Ok, entendi** ou
quando o limite volta ao valor anterior. A primeira leitura de uma conta não gera aviso, e uma leitura que falhou não
conta como mudança. Não há notificação do Windows.

**Status do Plus:**

| Status | Quando |
|---|---|
| **Ativo** | Resposta válida, validade no futuro |
| **Trial** | Ativo e o Roblox confirmou o período de teste (status ou campo de teste) |
| **Expirado** | Resposta válida com validade vencida, ou status encerrado |
| **Sem Plus** | Resposta válida, todas as páginas lidas, nenhum item `RobloxPlus`/`Blackbird` |
| **A conferir** | Leitura falhou ou veio num formato desconhecido; também quando a validade lida já passou e havia renovação confirmada (só uma nova leitura diz se renovou). Sempre com o motivo |
| **Expirando** (alerta) | Ativo/Trial, faltam até 7 dias para a validade e o Roblox não confirmou a renovação |

**Renovação confirmada** é qualquer um destes:
- status `Renewing`;
- um campo de renovação automática verdadeiro;
- uma data de próxima renovação no futuro.

`Cancelled` ou `cancelAtPeriodEnd` confirmam que **não** renova. Validade e renovação são datas diferentes: a tela oficial mostra "Renova 11 de dez.", enquanto o Limited Desk leu validade até 14/12 22:24.

**Quando atualiza:**
- em segundo plano, enquanto o app estiver aberto e desbloqueado, para contas com sessão disponível cuja última tentativa ocorreu há 25 minutos ou mais ou que ainda não foram consultadas;
- ao abrir uma conta que já precisa de uma nova consulta;
- manualmente pelo botão do resumo, em *Atualizar dados* nos detalhes da aba Plus ou em *Atualizar tudo* na lista.

**Limite de pedidos do Roblox (HTTP 429):** as contas são lidas uma por vez, com uma pausa curta entre elas. Se o
Roblox responder 429, todas as leituras param por pelo menos 1 minuto (ou pelo tempo que o Roblox pedir). A pausa
dobra a cada 429 seguido, até 10 minutos, e volta ao normal na primeira leitura bem-sucedida. A barra de status
mostra a hora da retomada. Quando a pausa termina, a conta recusada é lida de novo sozinha, sem esperar os 25
minutos. No resumo, o motivo aparece uma vez só ("o Roblox limitou os pedidos agora (HTTP 429)…"), sem repetir
"erro na leitura da cota" nem "com mais de 30 minutos".

## Diagnóstico da leitura do Plus

No painel de detalhes da aba Plus (cartão Diagnóstico → *Mostrar ferramentas*), o diagnóstico consulta as assinaturas com a sessão da própria conta e mostra três coisas:

1. **Identidade:** a sessão salva no cofre e a sessão do navegador do app (se a aba da conta estiver aberta), cada uma comparada com a conta aberta no app. Responde "conta confere", "pertence a OUTRA conta" ou "sessão recusada", sem mostrar IDs. A consulta usa a sessão do navegador quando ela confere, e senão a do cofre.
2. **Assinaturas:** para cada página, o status HTTP, quantos itens de Plus foram encontrados, se há próxima página e os **nomes e tipos dos campos**, por exemplo `subscriptions[].expirationTimestampMs:number`.
3. **Leitura do parser:** ACEITA (status, validade, renovação, renovação confirmada) ou RECUSADA, com o campo e o motivo, por exemplo "assinatura: campo status não é texto".

O texto também vai para o `app.log` e pode ser copiado. Não contém cookies, valores da resposta nem dados de pagamento: de um campo como `paymentMethod.last4` só aparece o nome.

## O que é lido

Antes de tudo, a sessão é conferida em `users.roblox.com/v1/users/authenticated`. Se não
for a conta cadastrada, nada mais é lido e todos os campos ficam "a conferir".

| Dado | Endpoint (GET) | Campo |
|---|---|---|
| Saldo | `economy.roblox.com/v1/user/currency` | `robux` |
| Pendentes | `economy.roblox.com/v2/users/{id}/transaction-totals?timeFrame=Month&transactionType=Summary` | `pendingRobuxTotal` |
| Plus | `apis.roblox.com/subscriptions/v2/user/subscriptions?ProductType=RobloxPlus` | status e validade |
| Tetos | `apis.roblox.com/transfer/v1/robux-transfer/user-transfer-limit` | `dailyLimit`, `monthlyLimit`, `perTransferLimit` |
| Restrições | `apis.roblox.com/user-settings-api/v2/user-settings/settings-and-options-subset?requestedUserSettings=robuxTransferLimits` | `robuxTransferLimits.currentValue.daily` / `.monthly` |
| Histórico | `apis.roblox.com/transaction-records/v1/users/{id}/transactions?transactionType=CurrencyTransfer&limit=100&sortOrder=Desc` | por cursor |

O cookie fica no cofre e no `RobloxApi`, e só vai para `https://*.roblox.com`. Saldo,
histórico e checkpoint são gravados dentro do cofre criptografado.

## Cálculo

- **Teto efetivo:** `min(teto do Roblox, restrição configurada)`. Zero é um limite válido.
- **Saídas:** contam só as de `transferRole = Sender` com valor negativo (soma do valor absoluto). Entradas e registros futuros não contam.
- **Enviado hoje:** desde 00:00 em Brasília (`America/Sao_Paulo`). É a estimativa diária.
- **Enviado em 30 dias:** `(agora − 30 dias, agora]`. Cada saída sai da conta exatamente 30 dias depois.
- **Restante hoje** (o que aparece na tela) = `min(restante do teto diário, restante 30 dias)`, em que restante do teto diário = `max(0, teto diário efetivo − enviado hoje)`. Uma conta com a cota de 30 dias esgotada mostra **0** hoje, não o teto diário inteiro. O detalhe "limitado pela cota de 30 dias" aparece quando é o caso.
- **Restante 30 dias** = `max(0, teto mensal efetivo − enviado em 30 dias)`.
- **Pode enviar agora** = `max(0, min(saldo, restante hoje, restante 30 dias))`.
- **Teto por transferência:** só nos detalhes, separado da capacidade.
- **Pendentes:** campo próprio. Só entram no saldo quando o Roblox creditar.
- **Plus** (ver também "Status do Plus" acima):
  - **Produtos aceitos:** `RobloxPlus` e `Blackbird` (nome interno). **Todas as páginas** de assinaturas são lidas, por `nextPageCursor` ou `nextPageToken`.
  - **Validade:** vem de `expirationTimestampMs` (data ISO só se ele não existir). Ativa enquanto a validade estiver no futuro.
  - `Active`, `Subscribed`, `Renewing`, `Cancelled` ou item sem campo de status: a validade decide.
  - `Expired`, `Inactive` ou `Ended`: inativa. Com validade no futuro, é contradição e fica "a conferir".
  - Nenhum item de Plus em nenhuma página: inativa. Uma página que falhou: "a conferir", nunca "inativa".

### "A conferir"

Aparece em vez do número quando:
- a assinatura Plus está inativa, expirada ou não verificada;
- o histórico não cobre 30 dias;
- houve erro na cota;
- saldo, limites ou histórico têm mais de 30 minutos;
- **qualquer leitura da última tentativa** falhou, veio incompleta ou num formato desconhecido.

Nesses casos o valor anterior e a hora em que foi lido continuam guardados (aparecem no
tooltip), mas nunca são mostrados como atuais.

**O que conta como formato desconhecido:**
- campo obrigatório ausente;
- status de Plus com valor fora da lista acima, mesmo com data;
- `Cancelled` sem data de expiração;
- transação sem `id`, data, valor ou com `transferRole` diferente de `Sender`/`Receiver`;
- `amount` e `currency.amount` com valores diferentes;
- campo de paginação desconhecido (em vez de supor "última página").

Uma transação que não dá para classificar invalida o histórico inteiro dessa leitura, para
nunca subestimar o uso.

### Histórico

- **Leitura completa:** começa na página mais nova, sem cursor e sem histórico salvo. Está completa quando alcança um registro mais antigo que (início − 30 dias), ou quando o Roblox diz que não há próxima página. *Reler histórico completo (30 dias)*, no menu da conta, força uma leitura completa e ignora o histórico e o cursor salvos.
- **Leitura incremental:** também começa na página mais nova e para ao alcançar o período já coberto pelo histórico salvo. **Resultado = histórico completo salvo + registros novos**, deduplicados pelo ID estável.
- **ID estável da transação:** o primeiro entre `idHash`, `transactionId`, `id` e `purchaseToken` que existe em todos os registros e é **único** na página. Fica gravado com o histórico.
  - O mesmo ID com data ou valor diferente quer dizer que o campo não identifica a transação: a leitura vira "a conferir" e nada é descartado em silêncio. É a causa provável dos 400 da 0.3.1: se o `id` se repete entre transferências, as 9 saídas colapsavam na mais recente.
  - Um registro idêntico listado duas vezes (fronteira de página) conta uma vez.
- **Retomada:** uma leitura pausada (erro, 429, 15 páginas) só é retomada se começou na página mais nova. Uma página antiga alcançada a partir de outro cursor não prova que a janela está completa.
- **Ordem e paginação:**
  - **Cursor:** `nextPageCursor` (parâmetro `cursor`) ou `nextPageToken` (parâmetro `pageToken`).
  - **Ordem:** fora de ordem, página cheia sem cursor ou cursor repetido viram "a conferir".
- **Versão:** históricos gravados até a 0.3.1 são descartados e relidos do zero automaticamente.
- **Diagnóstico** (Detalhes, linha "Histórico…"): páginas, registros, saídas, entradas, repetidos ignorados, campo de ID usado, período e motivo da parada.

### Plus: falhas e diagnóstico

- **HTTP 400 em contas sem assinatura:** o pedido com o filtro `ProductType=RobloxPlus` pode voltar **400**. Nesse caso o app repete a mesma consulta oficial **sem o filtro**.
  - **"Sem Plus"** só aparece se essa resposta for uma lista válida sem item de Plus.
  - Se as duas consultas falharem, a Plus fica "A conferir" com o **código e a mensagem do Roblox**, por exemplo "HTTP 400 (código 5: No subscriptions found)", sem IDs nem segredos.
- **Teste grátis:** "Sem Plus · **Trial disponível**" só aparece quando a resposta traz um campo booleano de elegibilidade (`isTrialEligible`, `freeTrialEligible`, `canStartTrial`…) dizendo que sim. Campos contraditórios deixam a Plus "A conferir". Sem o campo, a tela diz "teste grátis: o Roblox não informou". **"Trial"** continua significando conta **em** período de teste.
- **Assinatura × oferta:** um item com produto Plus dentro de uma lista de assinaturas é uma assinatura. Fora dela (por exemplo em `offers`), só conta se tiver campos de assinatura (status, validade, renovação, `subscriptionId`); senão é uma oferta e não vira assinatura.
- **Teste grátis pela página oficial:** a API de assinaturas não informa o teste grátis, mas a página `roblox.com/plus` oferece "Try it for free". Enquanto a aba de uma conta estiver nessa página (também `/pt/plus` e afins), o app lê nas respostas da própria página **só o sim/não** de elegibilidade, a partir da primeira chamada.
  - **Com resposta:** o resumo mostra "Sem Plus · Trial disponível (página oficial)", válido por 24 h. Depois disso, é preciso abrir a página de novo.
  - **Quem prevalece:** se a API de assinaturas disser "não", vale a API. Conta com Plus ativo nunca recebe a oferta.
  - **Nomes reconhecidos:** além da lista fixa (`isTrialEligible`, `freeTrialEligible`, `canStartTrial`…), vale qualquer campo verdadeiro/falso cujo nome fale de **poder** ter o teste: tem "trial" e também "elig", "avail", "offer", "redeemable" ou começa com "can"/"show". Nomes de **estar** em teste (`isTrial`, `inTrialPeriod`) não contam.
  - **Pela própria página:** se nenhuma resposta tiver esse campo, o app confere se a página `/plus` mostra a oferta ("Try it for free", "free trial ends on…", "teste grátis"…). Isso acontece também quando você chega à página pelo menu do site. A página devolve só sim ou nada; o texto dela não é guardado.
  - **Sem campo reconhecido:** o diagnóstico com as chamadas da página fica pronto nos detalhes da conta, na aba Plus, para copiar.
- **Gravar página do Plus (60 s):** nos detalhes da conta, na aba Plus (cartão Diagnóstico). Ao iniciar, o app volta à aba Contas e abre essa conta no navegador. Anota as chamadas JSON que as páginas do roblox.com fazem na aba da conta, para descobrir de qual endpoint oficial vem o "Teste grátis". Guarda só método, caminho (IDs viram `<id>`), nomes de parâmetros, status HTTP e nomes e tipos de campos. Sem valores, cabeçalhos ou cookies.

- **Essencial × auxiliar:**
  - **Essenciais** (decidem Ativo, Expirado ou Sem Plus): o produto, a **validade** (`expirationTimestampMs`) e o **status**. Um problema aqui deixa a Plus "A conferir", com o motivo.
  - **Auxiliares:** renovação (`nextRenewalTimestampMs` e outros), flags de renovação e de teste. Um problema aqui vira **observação** na tela e no log, e o valor conta como "não informado". **Nunca bloqueia o status.** Na 0.3.5, `nextRenewalTimestampMs` = 0 derrubava a Plus inteira na altconta35.
- **Timestamp 0:** nos campos de data vale como "não definido" pelo Roblox, nunca 1970. Valores negativos são recusados.
- **Sem renovação informada:** a tela mostra "renovação não informada pelo Roblox". O alerta **Expirando** aparece nos últimos 7 dias, porque o Roblox não confirmou a renovação por nenhum campo.
- **Rastro no log:** cada leitura grava no `app.log` uma linha `[assinaturas] registro em …; campos usados: …; observações: …`, só com nomes de campos.

- **Registro da assinatura:** o tipo do produto (`RobloxPlus`/`Blackbird`) pode estar no próprio registro ou num objeto interno (ex.: `productInfo.productType`). Status, validade, renovação e teste são lidos do **registro inteiro**, ou seja, do item da lista que contém o produto, até 3 níveis abaixo. Na 0.3.4, o parser só olhava o objeto interno, e por isso a altconta35 ficava "sem status e sem expirationTimestampMs".
- **Pagamento ignorado:** sub-objetos de pagamento (`payment…`, `card…`, `instrument…`, `wallet…`) **nunca** são lidos. A validade de um cartão não pode virar validade da Plus.
- **Campo repetido:** o mesmo campo em dois lugares com valores diferentes é recusado ("aparece mais de uma vez com valores diferentes").
- **No diagnóstico:** a linha `Plus: registro em …; campos usados: validade ← …` mostra de onde veio cada valor, só pelo nome do campo.

- **Busca:** o item de Plus (`RobloxPlus` ou `Blackbird`) é procurado em qualquer ponto da resposta, em todas as páginas.
- **Validade:** vem de `expirationTimestampMs`. Na altconta35, essa data (11/12/2026) é a mesma "Renova 11 de dez." da página oficial, e é ela que o app mostra. Regra mantida.
- **Falha:** se a leitura falhar, o resumo mostra **"Plus a conferir: motivo"**, por exemplo "lista de assinaturas não encontrada" ou "Roblox respondeu HTTP 404". **"Inativa"** só aparece quando uma resposta válida da tentativa atual confirma isso.
- **Log local:** em cada leitura de assinaturas, o `app.log` registra só o **status HTTP** e os **nomes e tipos dos campos** (`subscriptions[].expirationTimestampMs:number`), sem nenhum valor. O mesmo vale para a primeira página do histórico e para qualquer leitura recusada.

## Parsers e testes

Os parsers ficam em `EconomyParsers` (`src/RbxSwitcher.Core/Roblox/RobloxApi.Economy.cs`).
**O código do Limited Desk não estava disponível**, então eles seguem o contrato de campos
descrito para o projeto. Os testes usam respostas fictícias em
`tests/RbxSwitcher.Core.Tests/Fixtures/`, uma por endpoint e com variações (restrição de
500, Plus cancelada, status desconhecido, transação sem papel, campo ausente…). Quando
houver respostas reais sanitizadas ou o parser do Limited Desk, basta trocar os fixtures e
ajustar `EconomyParsers`.

## Pendente de confirmação

- **Filtro de produto:** o pedido de assinaturas continua com `ProductType=RobloxPlus`, como no contrato. Se a altconta35 vier "a conferir" ou "inativa" mesmo com a 0.3.1, confira se o Limited Desk chama sem esse filtro ou com `ProductType=Blackbird`.
- **Nomes de paginação:** os nomes reais em `subscriptions` e `transaction-records` ainda precisam de uma resposta real sanitizada.

## Próximo passo: validar com uma conta sua

A integração **não está pronta** até passar por isto:

1. **Prepare:** abra o RbxSwitcher, vá à aba **Plus** e abra os detalhes da conta.
2. **Saldo:** clique em **Atualizar dados** e compare com o número de Robux no topo do roblox.com.
3. **Pendentes:** compare com "Pendentes" em roblox.com/transactions (resumo do mês).
4. **Limites:** compare Hoje, 30 dias e Por transferência com o que o Roblox mostra na tela de transferência de Robux. Se você tiver configurado uma restrição, ela deve aparecer no tooltip e reduzir o teto.
5. **Plus:** compare com a página de assinaturas da conta.
6. **Se algo vier "a conferir":**
   - Passe o mouse no valor para ver qual leitura falhou.
   - Na barra de endereço do app, abra o endpoint correspondente (todos são `*.roblox.com`, então abrem na sessão daquela conta). Exemplos: `https://economy.roblox.com/v1/user/currency` e `https://apis.roblox.com/transfer/v1/robux-transfer/user-transfer-limit`.
   - Copie o JSON e **troque IDs, nomes e cursores** antes de me enviar. O JSON não contém o cookie, mas o histórico de transações tem nomes de outras contas.
   - Mande também o `%LocalAppData%\RbxSwitcher\app.log` (ele não contém cookies).
