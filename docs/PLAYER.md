# RbxSwitcher e o aplicativo Roblox (Player)

**Sintoma:** em "Enviar Robux" no perfil de outro jogador, o site pede para abrir o aplicativo Roblox. O
aplicativo abre **deslogado**, mesmo com a conta autenticada no RbxSwitcher. O mesmo acontece num navegador
comum.

## Causa

**O aplicativo Roblox tem o próprio login, separado de qualquer navegador.** Um link só entra numa conta no
aplicativo se o roblox.com colocar nele um **ticket de login de uso único**. O link de "Enviar Robux"
muito provavelmente não traz esse ticket. Sem ele, o aplicativo usa o login que ele mesmo guardou; se não houver
nenhum, abre deslogado.

**Evidências:**

1. **Não é o isolamento do RbxSwitcher.** O sintoma se repete num navegador comum, que não tem nada do
   RbxSwitcher. O aplicativo nunca lê cookies de navegador nenhum: nem dos perfis InPrivate do RbxSwitcher, nem
   do Edge ou do Chrome.
2. **Não é a associação de protocolos.** O aplicativo **abre**, então o Windows encaminha o link ao programa
   certo.
3. **Como o site entra numa conta no aplicativo** (no botão "Jogar"), confirmado no código dos dois projetos
   estudados:
   - **Bloxkeep:** `ui-gpui/src/core/roblox_api/play.rs` e `roblox_launcher.rs:140-151`.
   - **MultiAlt:** `src-tauri/src/api/auth.rs` e `platform/windows/launch.rs:63`.

   O fluxo é este:
   - o site pede `POST auth.roblox.com/v1/authentication-ticket` com a sessão da aba (desde set/2026 com uma
     asserção do cliente);
   - recebe o ticket no cabeçalho `rbx-authentication-ticket`;
   - abre `roblox-player:1+launchmode:play+gameinfo:<ticket>+…`.

   O aplicativo resgata o ticket e cria **a própria sessão**. Links `roblox://…` de navegação do aplicativo
   não seguem esse fluxo.
4. **O que falta confirmar:** a URI exata de "Enviar Robux". Ela só existe no seu Windows. A 0.3.4 registra no
   `app.log`, a cada pedido, uma linha `[player] pedido de abertura: …` com o **tipo** do link e os **nomes** dos
   parâmetros, mas **sem o ticket e sem valores**. Exemplos:
   - `roblox · navigation/<…> · link do aplicativo, sem login` → **confirma a causa**: por desenho do Roblox, o
     link não carrega login.
   - `roblox-player · launchmode … · com ticket de login do roblox.com (oculto)`, e mesmo assim o app abre
     deslogado → o ticket foi gerado mas **não foi aceito pelo aplicativo**. Aí o problema é do lado do
     Roblox: aplicativo desatualizado, bootstrapper de terceiros registrado no protocolo, ou falha no
     resgate do ticket.

## Confirmação (altconta35, 0.3.9)

- **Link de "Enviar Robux":** `roblox://navigation/currency_transfer`, classificado como **link do aplicativo, sem login**. O programa registrado no Windows é `RobloxPlayerBeta.exe` em `%LocalAppData%\Roblox\Versions\…`, e ele abre.
- **Conclusão:** a causa está confirmada. O aplicativo abre com o login que ele mesmo guardou. Não é defeito do RbxSwitcher nem da associação de protocolos.
- **Página de Quick Login:** `https://www.roblox.com/crossdevicelogin`, usada até a 0.3.9, responde **404**. Desde a 0.3.10 o app:
  - tenta `https://www.roblox.com/crossdevicelogin/ConfirmCode`;
  - confere o status HTTP;
  - se der erro, abre **Configurações → Segurança** (`/my/account#!/security`) com as instruções.

  A página usada e o status ficam no `app.log` (`[quick login] …`).

## Login automático no aplicativo (0.3.11, liberado por você)

Você liberou uma exceção ao requisito "não construir links de autenticação". Agora, para links sem login (como
`navigation/currency_transfer`), a janela oferece **"Entrar como @conta e abrir"**. O fluxo é o mesmo
do botão "Jogar" do site:

1. **Sessão:** a sessão da aba é sincronizada com o cofre, e o app confere em `users/authenticated` que ela é
   **dessa** conta. Se não for, nada é pedido.
2. **Asserção do cliente:** o app a pede em `auth.roblox.com/v1/client-assertion/` (GET, ou POST com CSRF se o GET
   responder 405).
3. **Ticket:** `POST auth.roblox.com/v1/authentication-ticket/` com a asserção como corpo,
   `RBXAuthenticationNegotiation: 1`, `Origin`/`Referer` do roblox.com e CSRF. O ticket vem no cabeçalho
   `rbx-authentication-ticket`.
   - O pedido sai **sem o proxy do Windows**, porque o ticket fica preso ao endereço de quem pede e o aplicativo
     não usa proxy. Se não alcançar o Roblox assim, tenta pelo proxy.
   - Só são aceitos tickets com caracteres simples, para que um ticket não consiga alterar o link.
4. **Abertura:** `roblox-player:1+launchmode:app+gameinfo:<ticket>+…` é entregue ao Windows. O aplicativo entra
   nessa conta e abre na tela inicial. Depois, você clica em "Enviar Robux" de novo.

**Garantias:**
- O ticket vale uma vez e só existe na memória durante essa chamada. Nunca vai para log, cofre ou tela, e o
  `Redact` também o remove de qualquer texto.
- O cookie da conta continua só no RbxSwitcher e no roblox.com.
- Nada é feito sem o clique na janela. O RbxSwitcher **não envia Robux** e não confirma nada no aplicativo.
- Se o Roblox recusar (sessão expirada, 429, erro), a janela oferece o Quick Login.
- O aplicativo já aberto em outra conta deve ser fechado antes.

**Abrir a tela pedida pelo site (0.3.12):** quando o link original é de navegação do aplicativo (por exemplo
`roblox://navigation/currency_transfer`, gerado pelo "Enviar Robux" no perfil de um jogador), o botão vira
**"Entrar como @conta e abrir"**.
- **Sequência:** depois do login automático, o app espera a janela do Roblox aparecer (até 60 s), aguarda 8 s
  para o login terminar e entrega ao Windows **o mesmo link que o site gerou**, sem alteração. O aplicativo, já na
  conta certa, abre a tela de envio para o jogador escolhido.
- **Aviso clicável:** durante a espera, clicar no aviso cancela. Depois, clicar no aviso abre a tela de novo, por
  até 5 min (útil se o login demorou mais que o previsto).
- **Quem confirma:** o envio, o valor e a confirmação são feitos **por você, no aplicativo**. O RbxSwitcher só abre a
  tela. O ID do destinatário que vem no link nunca vai para o log, onde só aparecem os nomes dos parâmetros.
- **Limite:** não existe sinal oficial de "login concluído". Os 8 s são uma estimativa e, se não bastarem, basta
  clicar no aviso.

**Risco conhecido:** este é o mesmo pedido que o site faz, mas não é uma API pública. Ele mudou em set/2026
(passou a exigir a asserção do cliente) e pode mudar de novo. O resultado de cada tentativa fica no `app.log`
(`[ticket] …`, `[player] login automático …`), sem o ticket.

## A janela (0.3.15)

Mostra só o essencial: o que abre (ex.: **Abrir envio de Robux no Roblox**), a **conta selecionada**, um aviso
curto ("O Roblox será aberto com essa conta. Se o aplicativo já estiver aberto em outra conta, feche-o antes de
continuar."), a linha **"Confira o nome da conta no Roblox antes de enviar."** e os botões
**Entrar como @conta e abrir** / **Cancelar**. Recolhidos por padrão:

- **Mais opções:** *Só abrir o Roblox* (usa a conta que já está logada no aplicativo) e *Entrar pelo Quick Login*.
- **Detalhes técnicos:** o pedido do site, se o link traz login ou não, o programa que o Windows abre e o lembrete
  de que o RbxSwitcher não envia Robux.

## Respostas às perguntas da investigação

1. **Armazenamento e isolamento:**
   - Cada conta guarda **só** o cookie de sessão, no cofre (AES-256-GCM + DPAPI + senha mestra opcional).
   - Cada aba é um perfil WebView2 **InPrivate** com nome único.
   - O aplicativo Roblox não enxerga nada disso, nem deve.
2. **Como o navegador do app trata `roblox://` e `roblox-player:`:**
   - **Até a 0.3.3:** a navegação era permitida e o WebView2 mostrava o aviso padrão dele, entregando o link ao
     Windows sem alteração.
   - **Na 0.3.4:** o evento `LaunchingExternalUriScheme` é interceptado (e também `window.open` para esses
     esquemas, que antes era descartado em silêncio). Quem decide passa a ser a confirmação do RbxSwitcher.
3. **Qual URI "Enviar Robux" gera:** a ser confirmada pela linha `[player]` do log (item 4 da Causa).
4. **Como o Windows encaminha o link:**
   - Pela chave `Software\Classes\<esquema>\shell\open\command` (primeiro a do usuário, depois a global).
   - A janela de confirmação e o log mostram qual programa está registrado (ex.: `RobloxPlayerBeta.exe` em
     `%LocalAppData%\Roblox\Versions\…`) e se o arquivo existe.
   - Um bootstrapper de terceiros registrado no lugar do Roblox aparece aqui.
5. **Ausência de login, protocolo ou falha do Roblox?** Pelas evidências, **ausência de login no aplicativo**: o
   link não traz ticket e o aplicativo não tem login próprio. A linha `[player]` confirma ou desmente isso.
6. **Mecanismos oficiais para levar o login ao aplicativo:**
   - **Entrar direto no aplicativo:** senha, 2FA, chave de acesso.
   - **Quick Login:** o aplicativo mostra um código; numa sessão já logada, o código é aprovado na página do
     roblox.com (`roblox.com/crossdevicelogin`) ou em Configurações › Quick Log In do aplicativo.
   - **Ticket de uso único do botão "Jogar":** gerado **pelo próprio site** quando ele decide abrir o aplicativo
     logado.
   - **O que o RbxSwitcher não faz:** pedir esse ticket por conta própria e montar o link `roblox-player:`, que
     é o que os gerenciadores estudados fazem. Seria um "link de autenticação improvisado", que os requisitos
     proíbem.
7. **Verificar a conta logada no aplicativo antes de continuar:** **não é possível de forma oficial.** O
   aplicativo guarda o login num armazenamento próprio e cifrado, e ler esse armazenamento seria mexer em
   credenciais dele. A conta tem de ser conferida **pela pessoa, no próprio aplicativo**. O RbxSwitcher diz qual
   conta está selecionada e avisa para conferir.

## O que a 0.3.4 faz

Quando um site do roblox.com, por um clique seu, pede para abrir o aplicativo, aparece uma confirmação do
RbxSwitcher com:
- a **conta selecionada** na aba (`@usuário`);
- se o link **traz login** (ticket do site, oculto) ou **não traz** (o aplicativo usa o login dele);
- o **programa** que o Windows vai abrir e se ele existe;
- o aviso: o RbxSwitcher não envia Robux nem confirma nada no aplicativo; confira a conta no app.

**Opções:**
- **Abrir o Roblox:** entrega o link ao Windows **exatamente como o site gerou**.
- **Abrir e entrar no app como @usuário:** só para links sem login. Abre o aplicativo e leva a aba dessa conta à
  página oficial do Quick Login. Você digita ali o código que o aplicativo mostrar e confirma; o aplicativo
  passa a ter a sessão dele, nessa conta.
- **Cancelar.**

**Pedidos recusados sem perguntar:**
- esquema diferente de `roblox`/`roblox-player`;
- link mal formado ou grande demais;
- origem que não seja `https://*.roblox.com`;
- pedido sem clique seu.

**Garantias:**
- A autenticação das contas não muda.
- Nenhum cookie, ticket ou token é criado, copiado para o aplicativo ou gravado em log. O `Redact` remove
  `roblox-player:…`, `gameinfo:…` e `ticket=…` de qualquer linha.
- O isolamento das abas continua o mesmo.

**Testes:** `tests/RbxSwitcher.Core.Tests/PlayerLinkTests.cs`, 19 testes. Cobrem:
- links com ticket, sem ticket e do aplicativo;
- formato `roblox-player://1/1`;
- recusas;
- a regra de origem e clique;
- a garantia de que nem o ticket nem IDs aparecem no resumo ou no log.

## Pendente de confirmação no Windows

- **URI de "Enviar Robux":** a linha `[player]` do `app.log`.
- **Página do Quick Login:** que `crossdevicelogin/ConfirmCode` abre a tela de confirmar código (a linha
  `[quick login]` do log mostra o status). Se não abrir, o app cai em Configurações → Segurança.
