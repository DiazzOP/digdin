# RbxSwitcher

Gerenciador de **muitas contas Roblox** para Windows. Você entra em cada conta uma vez,
pelo site oficial, e depois alterna entre elas com um clique, sem digitar a senha de
novo enquanto a sessão for válida. Cada conta roda num perfil de navegador isolado, e as
outras contas continuam logadas.

> Projeto independente, sem relação com a Roblox Corporation. Não modifica o Roblox nem
> o recurso oficial "Switch Accounts", não contorna CAPTCHA nem 2FA e não usa serviços de
> terceiros.

## Como usar

1. **Requisitos:**
   - Windows 10 ou 11, 64 bits.
   - [WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/). Já vem no Windows 11 e na maioria dos Windows 10.
2. **Rodar:** descompacte `RbxSwitcher-0.8.12-win-x64.zip` e abra `RbxSwitcher.exe`. Não precisa instalar nada nem ser administrador.
   - O executável não tem assinatura digital, então o SmartScreen avisa. Clique em *Mais informações → Executar assim mesmo*, ou compile você mesmo (abaixo).
3. **Primeira vez:** o app oferece criar uma **senha mestra** (recomendado).
4. **+ Adicionar conta:** entre pelo site do Roblox que aparece. A conta é salva sozinha quando o login termina.
5. **Trocar de conta:** clique na conta na lista.
   - Botão direito na conta: apelido, grupo, entrar de novo, remover.
   - Atalhos: `Ctrl+F` busca, `Ctrl+L` endereço, `F5` recarrega (na aba Plus, `F5` atualiza a conta selecionada).

**Abas:** no topo ficam as abas **Contas**, **Plus**, **Avatares**, **Distribuir**, **Limiteds** e **Calendário** e os botões **Bloquear** e **Configurações**.
- **Contas:** o navegador fica à direita. À esquerda, o resumo da conta ativa (Saldo · Pendentes · Pode enviar agora · Restante hoje · Restante 30 dias · Plus) e, logo abaixo, a lista para trocar de conta. Clicar no status do Plus abre a aba Plus já nos detalhes dessa conta.
- **Plus:** tabela com **todas as contas** (status do Plus, renovação ou validade, cota de hoje e de 30 dias), com busca e filtros por grupo e por status. **Ver detalhes** ou um duplo clique abre um painel lateral com assinatura, cotas, histórico de 30 dias e ferramentas de diagnóstico. **Atualizar tudo** consulta todas as contas na hora.
- **Avatares:** monte um avatar com itens grátis do Roblox e os da conta aberta (cabelo, acessórios, cabeça e rosto, camisa, calça, camiseta, corpo, tipo de corpo e tom de pele), sorteie, salve e aplique na conta aberta ou em várias contas, com um avatar sorteado por conta se quiser. Itens grátis que faltarem são pegos a preço 0; nada é comprado com Robux. Detalhes em [`docs/AVATARES.md`](docs/AVATARES.md).
- **Distribuir:** sugere qual Limited mandar das contas de origem (saques AstraFlip) para qual conta e com quais Smalls, explicando o porquê; abre a trade na conta certa; confere a trade no Roblox e refaz o plano; acompanha as vendas. Detalhes em [`docs/LIMITEDS.md`](docs/LIMITEDS.md).
- **Calendário:** quando os Robux pendentes das vendas de Limiteds devem cair em cada conta, dia a dia, com o valor. O Roblox não informa a data, então o app aprende o prazo de cada conta vendo as vendas passarem de pendente para creditada (ver [`docs/CALENDARIO.md`](docs/CALENDARIO.md)).
- **Atualização automática:** saldo, assinatura e cotas são consultados em segundo plano a cada **25 minutos por conta**, enquanto o app está aberto e desbloqueado (2 contas por vez).

Detalhes em [`docs/SALDO-E-COTA.md`](docs/SALDO-E-COTA.md). Inventário, trades e Limiteds ficam no Limited Desk.

**Aplicativo Roblox:** quando o site pede para abrir o aplicativo (por exemplo, em "Enviar Robux"), o
RbxSwitcher mostra qual conta está selecionada e se o link traz login. Se o aplicativo abrir deslogado,
oferece entrar nele automaticamente, com o mesmo login de uso único do botão "Jogar" do site, e depois abrir no aplicativo a mesma tela que o site pediu (por exemplo, o envio para aquele jogador). O Quick Login oficial continua como alternativa. Detalhes em [`docs/PLAYER.md`](docs/PLAYER.md).

**Status das contas:**
- 🟢 sessão ativa
- 🔴 precisa entrar de novo
- 🟡 o Roblox não respondeu
- ⚪ ainda não verificada

## Atualizações dentro do app (opcional)

O canal de atualização vem **desligado**. Para que novas versões cheguem sem baixar zip:

1. Publique este código num repositório **seu** no GitHub. O workflow `.github/workflows/release.yml` publica sozinho: a cada envio para `main` com uma versão nova em `Directory.Build.props`, ele testa, compila e cria o release `vX.Y.Z` com `RbxSwitcher.exe` + `RbxSwitcher.exe.sha256`.
2. No app, use **Configurações → Atualizações…** e informe `seu-usuario/seu-repositorio`.

O app procura versões a cada 6 horas e avisa. Ele só instala depois que você confirmar e se o SHA-256 conferir. Só fala com o GitHub desse repositório e nunca envia dados de conta.

## Onde ficam os dados

`%LocalAppData%\RbxSwitcher\`
- `vault.bin` e `vault.bin.bak`: contas e sessões, criptografadas.
- `app.log`: log sem cookies.
- `market-cache.json`: dados públicos de mercado dos Limiteds (Rolimons e vendas/dia do Roblox).
- `avatar-catalog.json`: catálogo público de itens grátis do Roblox (aba Avatares), renovado a cada 24 h.
- `WebView2\`: arquivos temporários do navegador, apagados a cada abertura.

## Segurança em resumo

- **Senhas:** o app nunca vê nem guarda senhas. Você entra no site oficial.
- **Cookies de sessão:** ficam num cofre AES-256-GCM. A chave é protegida pelo **DPAPI do Windows** e, se você definir, pela **senha mestra** (PBKDF2-SHA256, 600 mil iterações).
- **Isolamento:** cada conta abre num perfil **InPrivate** próprio. Nada da sessão é gravado fora do cofre.
- **Para onde vão os cookies:** só para `*.roblox.com`. Não há telemetria nem servidor próprio.
- **Navegador:** DevTools desligado, cookie `HttpOnly`, navegação limitada ao roblox.com e o "Sair" do site bloqueado nas contas salvas.

Detalhes, ameaças e limitações estão em [`docs/ANALISE.md`](docs/ANALISE.md).

## Compilar a partir do código

Precisa do .NET 8 SDK. Funciona em Windows, Linux ou macOS; o resultado roda só no Windows.

```bash
dotnet test
```

```bash
dotnet publish src/RbxSwitcher.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -p:AllowedReferenceRelatedFileExtensions=none -o dist/RbxSwitcher
```

## Estrutura

| Pasta | Conteúdo |
|---|---|
| `src/RbxSwitcher.Core` | Cofre, contas, API do Roblox, verificação de sessões, logs. Sem interface, testável |
| `src/RbxSwitcher.App` | App Windows: WinForms + WebView2 |
| `tests/RbxSwitcher.Core.Tests` | 252 testes automatizados, com respostas fictícias em `Fixtures/` |
| `docs/ANALISE.md` | Engenharia reversa do Bloxkeep e do MultiAlt, arquitetura, segurança e plano de testes |
| `tools/UiPreview` | Ferramenta de desenvolvimento: abre o app com contas fictícias no Wine e tira prints das telas |
| `THIRD-PARTY-NOTICES.md` | Licença MIT do Bloxkeep, cujo design system serviu de referência visual |

## Estado

**0.8.12.** Distribuir: etapas "Aguardando Small" e "Aguardando liberação" dizem quando cada cópia sai do holding (2 dias após trade, 7 após compra) e quando "Abrir trade" liga; um Small que ainda vai chegar diz de qual etapa vem. "Atualizar leituras" também lê as trades e compras das contas com itens em holding. **0.8.11.** Senha mestra fora do caminho: o botão Bloquear e o convite para criar senha saíram; quem tem senha vê só "Remover senha mestra…" em Configurações (depois disso o app abre direto; o cofre continua criptografado pelo Windows). **0.8.10.** Pré-seleção da trade: item fora da página aberta é procurado pela lupa (Pesquisar) do próprio inventário, que olha todas as páginas; as setas de página que não reagem ao clique recebem a sequência do ponteiro (botões novos do Roblox); depois de trocar de página, o app espera os cartões chegarem, não só o rótulo. A busca é limpa no fim. **0.8.9.** Pré-seleção da trade acha itens da página 2 em diante: as setas de página são achadas pelo rótulo "Página N" (na página real são só ícones) e a troca de página espera até 4 s. **0.8.8.** Pré-seleção da trade: um item já selecionado não é mais tirado da oferta por um segundo clique (a página real marca só com um ✓ e mostra o item na oferta sem a série; o app agora percebe isso e espera até 4 s). **0.8.7.** Distribuir: quadro "Destinatárias" em Sugestões (espaço, Smalls livres e em holding, trades sugeridas e o que bloqueia cada conta); uma destinatária com um só Small recebe também a trade de outra origem, que espera o Small que a primeira trade leva ("Aguardando Small"); descartar uma sugestão a apaga (não vai para o Histórico); os descartes viram uma linha só em "Ficaram de fora", fora da contagem. **0.8.6.** Distribuir: descartar uma sugestão vale só para aquela destinatária (a cópia pode ir para outra); "Voltar todas ao plano" em "Ficaram de fora"; mudar as origens ou as destinatárias em Configurar… limpa os descartes e refaz as sugestões. **0.8.5.** Distribuir: sugestão descartada não volta mais ao atualizar as leituras; as cópias dela ficam em "Ficaram de fora" ("sugestão descartada por você") com o botão "Voltar ao plano", e o espaço e os Smalls liberados já vão para as outras sugestões. **0.8.4.** Distribuir → Configurar…: destinatárias escolhidas conta a conta (o grupo marca as contas dele; desmarque para pausar uma conta, marque para incluir outra). **0.8.3.** Aba Limiteds: ao clicar numa cópia em holding, o painel mostra a data e a hora em que ela sai do holding (2 dias depois de receber por trade, 7 dias depois de comprar na loja), quanto falta e de onde veio a data. O app lê as compras e as trades concluídas das contas com cópias em holding, anota a hora exata das trades aceitas no app e quando viu cada cópia pela primeira vez. **0.8.2.** Aba Limiteds: fim dos "Livre · não lido" em massa. Uma única resposta 404 (ou três falhas seguidas) parava a leitura dos anúncios de todas as contas até fechar o app; agora uma falha vale só para o item que falhou. Os ids dos itens no Marketplace são buscados em lote e guardados; sem o id, o item não é lido nos endereços antigos do Roblox (que davam 404 e "0/dia" congelado). Os menores preços são lidos antes dos anúncios, ficam guardados no vault e valem depois de reabrir o app; com o Roblox limitando pedidos, a leitura espera e continua em vez de parar; o item lido há mais tempo vem primeiro. **0.8.1.** Calendário revisto (UI/UX): indicadores no topo (pendentes, hoje, próximos 7 dias, próxima queda — clicáveis), explicação do prazo numa linha com "Como é estimado"; grade só com as semanas do mês, que encolhe sem cortar (1366×768 a 125 % e 150 %), dias com valor em verde suave com barra do tamanho do valor, "atrasada" com ponto e palavra, legenda, dica ao passar o mouse, foco visível e atalhos (setas, Home, Page Up/Down); filtro por conta (lista "Conta" ou clique em "Por conta"); painel do dia como o da aba Limiteds (imagem do item, valor à direita, situação); estado de primeiro uso. **0.8.0.** Nova aba **Calendário**: quando os Robux pendentes das vendas de Limiteds devem cair em cada conta, num calendário do mês (valor por dia, vendas de cada dia, próxima queda por conta). As datas são estimadas: o app aprende o prazo de cada conta com as vendas que vê passar de pendente para creditada (sem histórico, usa o que as vendas lidas mostram ou 7 dias). As vendas passam a ser lidas dos últimos 31 dias, também nas contas que só têm Robux pendentes. **0.7.14.** Revisão do Distribuir: cópias que só a Rolimons lista não são sugeridas de novo depois de uma trade concluída (a Rolimons demora para atualizar) e as recebidas contam no espaço da destinatária; 2 cópias iguais sem id numa trade (ex.: Molten Lava Wings) conferem certo; "Localizar trade" não pega a trade de outra etapa e "Informar ID…" avisa; a trade enviada/aceita vai para a etapa com que bate; "Abrir trade" também exige o saldo da destinatária lido há menos de 30 min; cartões redesenham quando a etapa muda de Small; cópias em holding no resumo e no motivo de "Ficaram de fora"; a pré-seleção espera a página mostrar o clique antes de tentar de novo. **0.7.13.** Distribuir: Limiteds em holding nas origens entram no plano numa etapa "Aguardando liberação" (espaço e Small já reservados, "Abrir trade" desligado até sair do holding), depois das cópias liberadas e sem atrasar as trades que podem ir agora. **0.7.12.** Limiteds novos (UGC), que a lista de colecionáveis do Roblox não traz, entram pela lista da Rolimons e são conferidos com as cópias da conta no Marketplace (sumiu → sai; em holding → "Em holding"). Falha da Rolimons aparece no cabeçalho da conta. **0.7.11.** Limiteds em holding passam a aparecer: a lista de colecionáveis do Roblox omite cópias em holding (ex.: saques que chegaram ontem na FonteAlfa); o app completa com as cópias em holding que a Rolimons lista para a conta. **0.7.10.** Aba Limiteds volta ao layout anterior (sem cabeçalho de conta só com Smalls) e não deixa rastros de texto ao redesenhar. **0.7.10.** Small só quando todos os sinais de preço conhecidos (RAP de mercado, RAP do inventário, valor Rolimons) estão dentro do limite — um RAP errado do Marketplace escondia Limiteds da FonteAlfa como Smalls. Aba Limiteds com o layout anterior e sem rastros ao redesenhar. **0.7.9.** Itens com RAP 0 ou sem RAP (recém-migrados ao Marketplace) não são mais tratados como Smalls — era por isso que a Alfa e a Beta sumiam da aba Limiteds. Conta só com Smalls ocultos aparece com "N Smalls ocultos". **0.7.8.** Aba Limiteds: conta cujos Limiteds não foram lidos (ou leitura incompleta/falha) aparece no fim com o motivo, em vez de sumir. **0.7.7.** Total da aba Limiteds sem depender dos filtros: todas as contas (origens como Alfa e Beta inclusas, mesmo em holding), sem Smalls, mais os pendentes de todas as contas. **0.7.6.** Aba Limiteds: linha de total — líquido de todos os Limiteds das contas mostradas (à venda, prontas, em holding) + Robux pendentes. **0.7.5.** Aba Limiteds: datas de venda e de chegada no horário de Brasília (apareciam em UTC, um dia à frente depois das 21h). **0.7.4.** Aba Limiteds: vendas por dia dos itens do Marketplace lidas com a sessão de uma conta que tem o item (como o site faz), nunca mais o "0/dia" congelado do endpoint antigo; o RAP da Rolimons volta a ter prioridade; sem contagem de vendas, mostra a demanda da Rolimons. **0.7.3.** Aba Limiteds com os endpoints que a própria página do Roblox usa (confirmados pela gravação): lê os seus anúncios de verdade (inclusive acima dos menores preços), os 100 menores preços de cada item e as vendas por dia dos itens do Marketplace. A contagem da gravação aparece na barra de status. **0.7.2.** Aba Limiteds: os contadores dos filtros atualizam junto com a lista; o aviso "anúncios: HTTP 404" sai das contas (a leitura que falha para depois de 3 tentativas). **0.7.1.** Aba Limiteds com os dados reais: o Roblox respondeu 404 à leitura das cópias à venda, então "à venda / superada" e o seu preço vêm da lista dos 10 menores preços (onde a conta aparece como vendedora); a leitura com 404 não é repetida. Vendas por dia para os itens de todas as contas. **0.7.0.** Nova aba **Limiteds**: todos os Limiteds de todas as contas numa lista (por conta ou por item), com situação de venda — superada, à venda, pronta, em holding, vendida —, menor preço de outro vendedor, sugestão de preço com piso e a cota de cada conta. Só leitura: "Vender"/"Mudar preço" abrem o item na conta certa (ver `docs/LIMITEDS-VENDA.md`). **0.6.9.** Etapas da aba Distribuir com as imagens dos itens dos dois lados da trade, líquido no cabeçalho e a parte técnica (motivos e histórico) em "Detalhes", recolhida. Histórico sem linhas repetidas; outra cópia do mesmo Small não aparece mais como "trocado". **0.6.8.** Depois que você envia a trade da etapa no site (com 2FA, se pedir), o app confere a trade, troca para a conta destinatária e abre a trade em Trocas para você aceitar; quando aceita, confere de novo e conclui a etapa. Se a trade não bater, não troca de conta e avisa. O app nunca envia nem aceita. **0.6.7.** Pré-seleção funciona já na primeira "Abrir trade" depois de abrir o app (o carregamento inicial da aba, cancelado, era tomado como falha da página de trade) e espera a página terminar de montar os inventários. **0.6.6.** Pré-seleção da trade ajustada à página real: os lados são achados pelos títulos ("Seu inventário", "Inventário de <nome de exibição>"), cópias iguais sem série são equivalentes (marca a primeira) e a conferência aceita outra cópia do mesmo Limited quando o Roblox informa qual foi. **0.6.5.** Revisão da pré-seleção da trade: nenhum clique segue link (a página de trade não se perde), a cópia que vai para a oferta não é clicada de novo, inventários grandes são percorridos até o fim, itens com nomes como "Counterfeit" são marcados, página recarregada ou tempo esgotado aparecem com o motivo certo. **0.6.4.** "Abrir trade" já deixa os itens da etapa marcados na página de trade (Limiteds e Small da origem, Small da destinatária); o app nunca envia — você confere e envia. Pode desligar em Configurar…. **0.6.3.** Distribuir: cada sugestão mostra a conta do espaço da destinatária (teto − ocupado, com saldo, pendentes, Limiteds parados e lotes a caminho, e o espaço de agora ao lado do "na venda"); o cartão do Plus mostra as mesmas parcelas. **0.6.2.** Avatares: corrige "LimitExceeded" ao vestir (itens demais de um mesmo tipo, como duas sobrancelhas de pacotes diferentes). O app lê os limites por tipo do Roblox, corta antes de enviar e, se o Roblox ainda recusar, tira a peça apontada e tenta de novo; a recusa aparece com motivo em português. **0.6.1.** Revisão de código e design da distribuição: conferência de trades mais segura (série diferente não passa como a cópia planejada; trades v2 sem id da cópia conferem os Smalls pela série; ID errado não desliga a trade já ligada), vendas não se perdem atrás de vendas de game pass, reserva de Smalls conta só os liberados, cookie renovado guardado em "Localizar trade". Aba Distribuir com contagem nas abas internas, motivos recolhíveis nas etapas em andamento e textos sem "(s)". **0.6.0.** Distribuição de Limiteds completa: aba Distribuir com sugestões, Abrir trade, conferência e replanejamento, vendas (ver `docs/LIMITEDS.md`). **0.5.0:** etapa 1: inventário, valores, configuração e espaço por conta (ver `docs/LIMITEDS.md`). **0.4.3:** itens grátis pegos pelo Marketplace do Roblox (corrige "InvalidArguments" na aba Avatares). **0.4.2:** pausa automática e nova tentativa quando o Roblox limita os pedidos (HTTP 429). **0.4.1:** revisão de interface com telas reais (ver `docs/REVISAO-UI.md`). **0.4.0:** aba Avatares (editor, sorteio, avatares salvos, aplicar em lote). Aviso quando o limite de transferência diminui. Janela de abrir o Roblox simplificada. Visual das abas Contas e Plus adaptado do design system do Bloxkeep (MIT, ver
`THIRD-PARTY-NOTICES.md`). A lógica central é coberta por testes. A interface e o navegador foram
compilados para Windows, mas ainda **não foram testados num Windows real**: siga o roteiro
da seção 7 de `docs/ANALISE.md`. A leitura de saldo e limites ainda precisa ser validada com
uma conta real (roteiro no fim de `docs/SALDO-E-COTA.md`).
