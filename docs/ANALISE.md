# RbxSwitcher — Análise técnica e arquitetura

Este documento cobre os itens 1 a 7 do pedido. As afirmações sobre o Bloxkeep e o
MultiAlt foram conferidas no código-fonte público dos dois projetos (commits de
16/11/2026 e 15/11/2026), com o arquivo e a linha indicados. O que não foi possível
verificar está marcado como tal.

---

## 1. Análise técnica

### 1.1 "Switch Accounts" oficial do Roblox

- **O que é verificável sem contas reais:** o recurso é do próprio Roblox (site e app).
  Ele guarda, no mesmo dispositivo, várias sessões já autenticadas e troca entre elas sem
  pedir a senha de novo, com um número máximo de contas definido pelo Roblox.
- **O que não foi verificado:** o mecanismo interno (onde as sessões ficam, como o limite
  é aplicado) não é documentado publicamente. Para observá-lo seria preciso capturar o
  tráfego com contas reais, e isso não foi feito. Por isso este projeto **não depende** do
  recurso oficial nem tenta alterá-lo.
- **Conclusão prática:** cada sessão do Roblox no navegador é um cookie
  `.ROBLOSECURITY` no domínio `.roblox.com`. Contas diferentes precisam de "potes de
  cookies" diferentes. Qualquer gerenciador externo é, no fundo, um gerenciador de potes
  de cookies isolados. É exatamente isso que os dois projetos estudados fazem, e é o
  que este projeto faz.

### 1.2 Bloxkeep (Rust + GPUI + WebView2, licença MIT)

| Tema | O que o código faz | Onde |
|---|---|---|
| Armazenamento | Um arquivo `vault.rmv` com AES-256-GCM. A chave vem de PBKDF2-HMAC-SHA256 da senha mestra, com 600 mil iterações. O cabeçalho entra como dado autenticado (AAD) | `ui-gpui/src/core/vault.rs:1-5, 42, 349` |
| Persistência de sessão | Guarda só o cookie `.ROBLOSECURITY` de cada conta, dentro do cofre | `forms/website_tab.rs:41` |
| Isolamento | Cada aba é um perfil WebView2 **InPrivate** com nome único (GUID). Todas as abas compartilham um processo de navegador numa pasta em `%TEMP%`, apagada no fim | `forms/website_tab.rs:76-80, 536-537` |
| Alternância | Abre a aba da conta e injeta o cookie salvo pelo `CookieManager`, com `Secure` e `HttpOnly` | `forms/website_tab.rs:699-709` |
| Proteção contra logout | Intercepta `POST https://auth.roblox.com/v*/logout` e devolve 403. Assim, "Sair" no site não mata a sessão salva | `forms/website_tab.rs:712-750` |
| Rotação de cookie | Lê os cookies da aba periodicamente e salva o valor novo quando o Roblox troca o cookie | `forms/website_tab.rs:899-930` |
| Login | Abre o site oficial, captura o cookie quando o Roblox o define e confirma a conta | `forms/browser_login_form.rs:533` |
| DPAPI | Usado só para passar a chave do cofre durante um reinício do próprio app | `core/relaunch.rs:3-110` |
| Rede própria | Um contador de usuários online, ativo por padrão (pode ser desligado). Envia um ID anônimo e IDs de jogos, nunca cookies | `PRIVACY.md`, `core/app/online.rs` |
| Riscos | O instalador é um binário colocado direto no repositório, não compilado no CI. O executável não tem assinatura digital. Há um único mantenedor | `.github/workflows/release.yml:10-11` |

### 1.3 MultiAlt (Rust + TypeScript, Tauri, licença MIT)

| Tema | O que o código faz | Onde |
|---|---|---|
| Armazenamento | XSalsa20-Poly1305. A chave vem de Argon2i (128 MiB, t=6) aplicado sobre o SHA-512 da senha | `src-tauri/src/data/crypto.rs:66-80` |
| Chave sem senha | A chave mestra é embrulhada **duas vezes**: com DPAPI e com um "hash do aparelho", e **qualquer uma abre**. O próprio código admite que só a senha protege contra quem tem acesso ao perfil do Windows | `data/vault_key.rs:30-50` |
| Navegador | Baixa um Chromium (Chrome for Testing) e o abre com `--user-data-dir` por perfil. Controla o navegador pelo **CDP** (porta de depuração em `127.0.0.1`) | `chromium/cdp.rs:26-53`, `chromium/download.rs` |
| Risco do CDP | Enquanto a porta de depuração está aberta, qualquer processo local pode ler os cookies (o comentário do próprio código admite isso) | `chromium/cdp.rs` (cabeçalho) |
| "Stealth" | Opcional: esconde `navigator.webdriver`, ou seja, disfarça a automação | `chromium/cdp.rs:445-453` |
| Rotação | Lê `Set-Cookie: .ROBLOSECURITY` de qualquer resposta da API e grava o valor novo | `api/cookie_rotation.rs` |
| Validação | Chama `users.roblox.com/v1/users/authenticated` | `api/auth.rs:54-106` |
| Recursos arriscados | Gerador de contas via serviço pago de terceiros (BloxGen). Integração com *executors* | `GeneratorDialog.tsx`, `NexusPage.tsx` |
| Ponto forte | O instalador é compilado no GitHub Actions e as atualizações são assinadas | `.github/workflows/release-v4.yml` |

### 1.4 O que aproveitamos e o que evitamos

- **Aproveitamos (como ideia, sem copiar código):**
  - Do Bloxkeep: perfil InPrivate por aba com cookie injetado, bloqueio do logout do site e sincronização da rotação.
  - Do MultiAlt: capturar rotações também nas respostas da API.
- **Evitamos:**
  - CDP e porta de depuração.
  - Baixar navegador próprio.
  - Disfarçar automação.
  - "Hash do aparelho" como alternativa à senha.
  - Telemetria.
  - Qualquer serviço externo.
  - Gerador de contas e executors.

---

## 2. Arquitetura recomendada

```
┌──────────────────────────── RbxSwitcher.exe (WinForms, .NET 8) ───────────────────────────┐
│  Lista de contas (owner-drawn, grupos, busca)        Barra: ◀ ▶ ⟳ Início  URL  Perfil …  🔒 ⚙ │
│                                                     ┌───────────────────────────────────────┐ │
│  BrowserHost ── LivePool (N abas vivas, LRU) ──────▶│ SessionTab = WebView2 InPrivate       │ │
│        │                                            │  perfil "t<GUID>" (1 por conta aberta)│ │
│        │  cookie injetado (HttpOnly, Secure)        │  • bloqueia POST /v*/logout           │ │
│        │  ◀── cookie lido a cada 20 s / ao fechar ──│  • DevTools desligado                 │ │
│        ▼                                            │  • só https://*.roblox.com            │ │
│  RbxSwitcher.Core (net8.0, testável, sem UI)        └───────────────────────────────────────┘ │
│   ├ VaultFile/VaultCrypto: AES-256-GCM, chave ← DPAPI [+ PBKDF2(senha mestra)]                │
│   ├ AccountStore: chave = userId do Roblox (sem duplicatas), grupos, busca                     │
│   ├ RobloxApi: users/authenticated, thumbnails, logout (só *.roblox.com, sem redirect)          │
│   ├ SessionChecker: 3 em paralelo, espaçado, respeita 429                                       │
│   └ Redact + Log: nenhum cookie chega ao disco fora do cofre                                    │
└───────────────────────────────────────────────────────────────────────────────────────────────┘
          %LocalAppData%\RbxSwitcher\  vault.bin (+ .bak)   app.log   WebView2\ (apagada ao abrir)
```

**Por que WinForms + WebView2 em C#:**
- O WebView2 tem suporte oficial a perfis isolados e InPrivate e a `CookieManager`, e a API para .NET é a mais madura.
- O DPAPI já vem no .NET (`ProtectedData`).
- Não há dependências além do pacote oficial `Microsoft.Web.WebView2`.
- A interface fica em código, sem arquivos de designer. Por isso o projeto compila também em Linux/CI.

**Escala (100+ contas):** cada conta salva custa só alguns KB no cofre. Só as N contas
usadas mais recentemente (5 por padrão, configurável de 1 a 20) mantêm um navegador vivo.
As outras são reabertas em cerca de 1 segundo, a partir do cookie salvo.

---

## 3. Alternância de sessões: mecanismo escolhido

1. **Adicionar conta:**
   - O app abre uma aba InPrivate limpa em `https://www.roblox.com/login`.
   - Você entra pelo site oficial. Senha, 2FA e CAPTCHA são tratados pelo Roblox; o app não vê nada disso.
   - O app verifica o cookie da aba a cada segundo. Quando `.ROBLOSECURITY` aparece, pergunta ao Roblox de quem é (`/v1/users/authenticated`).
   - Se a conta já estiver salva (mesmo `userId`), só atualiza a sessão. Senão, adiciona.
   - A aba vira a aba daquela conta.
2. **Trocar de conta:**
   - Se a aba da conta está viva, ela só é trazida para a frente (instantâneo).
   - Senão, o app cria um perfil InPrivate novo e injeta o cookie salvo como `HttpOnly` e `Secure` antes da primeira navegação.
   - As outras abas não são tocadas: cada perfil tem seu próprio pote de cookies.
3. **Manter válido:**
   - O Roblox às vezes troca o cookie. O app lê o cookie de cada aba viva a cada 20 s e também antes de fechá-la, bloquear o app ou sair.
   - Quando o valor muda e o Roblox confirma que é a mesma conta, o cofre é atualizado.
4. **Persistir:** só o cofre criptografado guarda as sessões. Ao reabrir o app, todas as
   contas voltam e são verificadas em segundo plano: 🟢 ativa, 🔴 precisa entrar de novo,
   🟡 sem resposta do Roblox.
5. **Expirou ou foi revogada:** a conta fica vermelha. Ao clicar, o app oferece "Entrar
   de novo", que é um login legítimo no site oficial. Nada é contornado.
6. **Não desconectar as outras:**
   - O "Sair" do site é bloqueado nas abas de contas salvas, porque encerraria a sessão no servidor.
   - Para tirar uma conta, use **Remover**. Ele oferece encerrar a sessão no Roblox de propósito.

---

## 4. Comparação de tecnologias

| Opção | Prós | Contras | Veredito |
|---|---|---|---|
| **WebView2 InPrivate por aba + cofre** (escolhida) | Isolamento real por perfil. Nada da sessão em disco fora do cofre. Senha mestra protege tudo. Escala para 100+ contas. Usa o Edge do sistema, que é atualizado pelo Windows | Precisa sincronizar a rotação do cookie. Cada aba viva custa cerca de 100–200 MB | ✅ Melhor equilíbrio |
| WebView2 com perfil persistente por conta | O próprio navegador guarda e rotaciona os cookies | Cerca de 100 pastas de perfil em disco, protegidas só pelo DPAPI do Chromium. Uma senha mestra não as protege. Verificar o status exige abrir o perfil | Alternativa válida, menos segura |
| Perfis isolados no Chrome/Edge do usuário (`--user-data-dir`) | Navegador "de verdade", sem embutir nada | Ler e gravar cookies exige CDP ou extensão (porta de depuração = risco de roubo local). Integração ruim e janelas separadas | Só como plano B, se o Roblox um dia bloquear o WebView2 |
| CEF / Electron / Chromium próprio | Controle total | Download de 100+ MB. É você quem precisa atualizar a segurança do navegador | ❌ |
| Cliente Roblox com múltiplas instâncias | — | Fora do escopo (você pediu o site) e altera o comportamento do cliente | ❌ |

**WebView2 e o login do Roblox:** o WebView2 é o motor do Edge (Chromium) com um user-agent
normal. O login do Roblox (senha, 2FA, CAPTCHA, código rápido) funciona nele; o Bloxkeep
usa o mesmo caminho. O app **não** esconde que é um navegador embutido e não altera
`navigator.webdriver`. Se o Roblox um dia recusar o WebView2, o plano B é a linha 3 da
tabela, abrindo o navegador do sistema sem porta de depuração.

---

## 5. Segurança

### Ameaças e proteções

| Ameaça | Proteção |
|---|---|
| Alguém copia o `vault.bin` para outro PC | A chave é embrulhada por DPAPI, que só abre nesta conta do Windows. Com senha mestra, é preciso também a senha (PBKDF2-SHA256, 600 mil iterações) |
| Outro programa rodando como você lê o cofre em disco | Sem senha mestra: possível, porque o DPAPI abre para qualquer processo do usuário. **Com senha mestra: não**, porque a chave depende da senha |
| Alteração do arquivo | O AES-GCM autentica o conteúdo e o cabeçalho. Um arquivo alterado é recusado e o app usa o `.bak` |
| Cookie vazando em log ou tela | Todo log, status e mensagem de erro passa por `Redact`. O `Account.ToString()` não inclui o cookie (há testes para isso) |
| Golpe do "cole isto no console" | DevTools desligado. O cookie é `HttpOnly`, então scripts da página não o leem |
| Página maliciosa ou redirecionamento | As abas só navegam em `https://*.roblox.com`. Outros sites abrem no navegador normal, **sem** a sessão. Pop-ups são bloqueados. Esquemas `file:`, `javascript:` e `data:` são bloqueados |
| Cookie enviado para fora | `HttpClient` sem pote de cookies compartilhado e sem seguir redirecionamentos. O cookie vai só no cabeçalho de chamadas para `users.roblox.com` e `auth.roblox.com`. Avatares são baixados **sem** cookie e só do CDN `*.rbxcdn.com` |
| Senhas | O app nunca vê nem guarda senhas. O preenchimento automático de senhas do WebView2 está desligado |
| Sessão de conta removida continua viva | "Remover" oferece encerrar a sessão no Roblox (`POST /v2/logout`) |
| Uso enquanto você está ausente | Bloqueio manual (botão **Bloquear**) e automático por inatividade (padrão: 15 min, exige senha mestra). Ao bloquear, todas as abas são fechadas e a chave sai da memória |
| Privilégios | Não pede administrador (`asInvoker`). Não pede para desativar antivírus. Não usa serviços externos nem telemetria |

### Limitações honestas

- **Memória:** com o app desbloqueado, os cookies ficam em memória. As strings do .NET não podem ser apagadas com garantia. Um malware rodando na sua conta naquele momento pode lê-los. Nenhum gerenciador de contas escapa disso.
- **Sem senha mestra:** o cofre está tão protegido quanto a sua conta do Windows.
- **Rotação perdida:** se o app travar entre duas sincronizações (até 20 s), a rotação pode se perder. Nesse caso a conta aparece como expirada e basta entrar de novo.
- **Mudanças do Roblox:** o Roblox pode mudar endpoints ou exigir verificações extras para sessões usadas de outro IP. O app trata isso como "precisa entrar de novo".
- **Executável sem assinatura:** o `.exe` não tem assinatura digital, então o SmartScreen vai avisar. A forma de confiar é compilar a partir do código-fonte.

---

## 6. Plano de implementação

**Estrutura**
```
src/RbxSwitcher.Core/      lógica pura (net8.0) — testada em CI
  Security/   VaultCrypto, IKeyProtector, Redact
  Storage/    VaultFile (gravação atômica + .bak), AppSettings
  Accounts/   Account, AccountStore
  Roblox/     RobloxApi
  Sessions/   SessionChecker, LivePool
  Logging/    Log
src/RbxSwitcher.App/       Windows (net8.0-windows, WinForms + WebView2)
  Platform/   DpapiProtector, Native (inatividade, barra de título escura)
  Browser/    SessionTab, BrowserHost
  UI/         Theme, AccountList, AvatarCache, Dialogs
  MainForm.cs, Program.cs
tests/RbxSwitcher.Core.Tests/   28 testes xUnit
```

**Etapas do MVP (feitas)**
1. Cofre criptografado com testes.
2. Cliente da API do Roblox com testes.
3. Aba InPrivate com injeção e leitura de cookie.
4. Pool de abas vivas.
5. Fluxo de login legítimo.
6. Lista com grupos, busca e status.
7. Bloqueio e senha mestra.
8. Empacotamento em `.exe` autocontido.

**Próximas etapas sugeridas**
1. Testar no Windows com contas reais (seção 7).
2. Assinatura de código e build no GitHub Actions (como o MultiAlt).
3. Importar e exportar o cofre (backup cifrado).
4. Arrastar contas entre grupos e ações em lote (mover vários, verificar só um grupo).
5. Opcional: abrir o cliente Roblox na conta selecionada pelo fluxo oficial "Jogar" do site.

---

## 7. Critérios de teste

### Automatizados (rodam em Linux/CI: `dotnet test`)

- **Cofre:**
  - 100 contas sobrevivem a fechar e reabrir.
  - O arquivo não contém cookie nem username em texto puro.
  - Outro usuário do Windows não abre o cofre.
  - Senha errada ou ausente é recusada.
  - Senha certa com outro usuário do Windows também é recusada.
  - Trocar ou remover a senha invalida a antiga, inclusive no `.bak`.
  - Um arquivo adulterado é recusado e o `.bak` assume.
- **Contas:**
  - Nada de duplicatas por `userId`.
  - Busca por username, display name, apelido e grupo.
  - A rotação só substitui o cookie esperado.
  - O `ToString` não vaza o cookie.
- **Logs:** nenhum cookie nem `x-csrf-token` chega ao arquivo de log.
- **API:**
  - O cookie vai só para `users.roblox.com` via HTTPS.
  - 401 vira expirada; erro 5xx não marca como expirada.
  - Lixo e tentativas de injeção de cabeçalho nunca são enviados.
  - Rotação via `Set-Cookie` é capturada.
  - O logout usa o CSRF.
  - Avatares fora do CDN do Roblox são rejeitados, e avatares nunca levam cookie.
- **Pool:** mantém as N contas mais recentes e nunca fecha a aba ativa.

### Manuais no Windows (o teste definitivo)

1. **Cadastro em massa:** adicione 50 contas por "+ Adicionar conta". Cada uma deve aparecer com avatar, nome e 🟢.
2. **Persistência:** feche o app (pelo X), abra de novo e desbloqueie. As 50 contas devem estar lá e ficar 🟢 em segundos.
3. **Alternância:** clique em contas aleatórias (ex.: 1 → 35 → 68 → 1). A barra superior mostra `@usuario`. Abra Perfil, Inventário e Trades. A página deve ser sempre da conta clicada.
4. **Isolamento:** com as contas A e B abertas, abra `https://users.roblox.com/v1/users/authenticated` em cada aba. Cada uma mostra o próprio `id`. Volte para A: continua A.
5. **Não desconecta as outras:** clique em "Sair" no site dentro de uma conta salva. Deve aparecer o aviso de bloqueio e a conta continua logada. As outras não mudam.
6. **Expiração:** num navegador comum, entre na conta X e use "Sair de todas as outras sessões". No app, "Verificar sessões" deve deixar X 🔴. Clique em X → "Entrar de novo" → X volta a 🟢 sem duplicar.
7. **Vazamentos:**
   - Abra `%LocalAppData%\RbxSwitcher\vault.bin` num editor: não deve haver `WARNING`, usernames nem cookies.
   - Procure em `app.log` por `WARNING:-DO-NOT-SHARE`: não pode existir.
   - A pasta `WebView2\` não deve ter cookies de contas depois de fechar (perfis InPrivate).
8. **Rede:** com o Monitor de Recursos ou o Fiddler, verifique que o app só fala com `*.roblox.com`, `*.rbxcdn.com` e os serviços do Edge/WebView2.
9. **Bloqueio:**
   - Defina a senha mestra e clique em **Bloquear**. As abas fecham e a lista some. Desbloqueie e tudo volta.
   - Copie o `vault.bin` para outra conta do Windows: não abre.
