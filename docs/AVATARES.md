# Avatares

Aba **Avatares** (Contas | Plus | Avatares): monta um avatar com itens grátis do Roblox e com os itens que a
conta aberta já possui, e aplica na conta aberta ou em várias contas.

## Na tela

- **Categorias** (chips no topo, com a quantidade): Cabelo, Acessórios, Cabeça e rosto, Camisa, Calça,
  Camiseta, Corpo. Busca por nome e **Mostrar:** Tudo, Grátis do Roblox ou Da conta.
- **Grade:** miniatura e nome; a etiqueta diz **grátis** ou **da conta**. Clique para escolher e clique de novo
  para tirar. Cabelo e camiseta: 1 item. Acessórios: até 4 (um por tipo). Cabeça e rosto: uma cabeça e um rosto.
  Corpo: um pacote de corpo.
- **Manter o atual** (padrão): a categoria fica como está em cada conta. **Nenhum**: tira tudo da categoria.
- **Coluna da direita:**
  - **Conta:** a conta aberta (a mesma da aba Contas) e o seletor para abrir outra.
  - **Imagem:** o avatar atual da conta.
  - **Seleção:** o que cada categoria vai receber. O **cadeado** trava a categoria para o "Sortear".
  - **Tipo de corpo:** Manter, R15 ou R6.
  - **Tom de pele:** 8 tons, ou o primeiro círculo para manter. **Travar tom** protege o tom do "Sortear".
  - **Sortear tudo:** um item em cada categoria não travada (0 a 2 acessórios, camiseta às vezes) e um tom de pele.
  - **Limpar:** volta tudo para "manter o atual".
  - **Aplicar em @conta** e **Aplicar em várias contas…**
- **Avatares salvos:** nome + **Salvar** (mesmo nome substitui). Clique carrega no editor; botão direito:
  carregar, aplicar na conta aberta, aplicar em várias, excluir. Ficam no cofre criptografado.

## Aplicar em várias contas

Janela com busca, grupo, **Marcar visíveis**/**Desmarcar** e a escolha:

- **Este avatar:** todas recebem o avatar do editor.
- **Sortear um por conta:** cada conta recebe uma combinação sorteada só entre os itens **grátis** do Roblox
  (que qualquer conta pode pegar). Categorias e tom travados ficam como no editor.

As contas são feitas **uma por vez**, com 1,5 s entre elas. Cada linha mostra o resultado (aplicado, aplicado em
parte, precisa entrar, falhou) e o detalhe ao passar o mouse. Se o Roblox limitar os pedidos (HTTP 429), o app
espera de 20 a 120 s e tenta a mesma conta de novo, até 3 vezes; se continuar, para e marca o resto como "não
aplicado". **Cancelar** para depois da conta em andamento. Bloquear o app também cancela.

## O que acontece em cada conta

1. Confirma que o cookie salvo é dessa conta (`users.roblox.com/v1/users/authenticated`). Se for de outra, nada é alterado.
2. Lê o que ela veste (`avatar.roblox.com/v1/avatar`).
3. Para cada item escolhido, confere se ela possui (`inventory.roblox.com/v1/users/{id}/items/{Asset|Bundle}/{id}/is-owned`).
   - Se não possui e o item é **grátis do Roblox**, pega o item pelo **Marketplace** (desde a migração de 2024 é
     por ali que o Roblox vende os próprios itens, inclusive os grátis):
     `apis.roblox.com/marketplace-items/v1/items/details` (produto e preço) e depois
     `apis.roblox.com/marketplace-sales/v1/item/{id}/purchase-item` com `expectedPrice: 0`. Quando a busca não traz o
     id do Marketplace, ele é lido em `catalog.roblox.com/v1/catalog/items/details`. O endpoint antigo
     (`economy.roblox.com/v1/purchases/products/{productId}`, também com `expectedPrice: 0`) é só a última tentativa:
     para itens migrados ele responde `InvalidArguments`, o erro visto na 0.4.2.
   - Se não possui e o item **não é grátis**, ele fica de fora. **O app nunca compra com Robux.**
   - Se o Roblox pedir **captcha**, o item fica de fora com esse motivo e os outros itens grátis daquela conta não são tentados. O app não contorna a verificação: pegue o item pelo site nessa conta.
4. Tipo de corpo (`set-player-avatar-type`), tom de pele (`v2/avatar/set-body-colors`, com o v1 como reserva).
5. Monta a lista completa e veste (`v2/avatar/set-wearing-assets`, com o v1 como reserva):
   - fica tudo o que a conta veste nas categorias "manter" e o que o editor não mexe (animações, emotes);
   - sai o que estava nas categorias "nenhum" ou com itens escolhidos;
   - pacotes vestem as próprias peças;
   - partes únicas (cabeça, rosto, camisa, calça, partes do corpo) ficam com uma só: item escolhido > peça de pacote > o que já estava.
   - **Limite por tipo:** o Roblox só deixa vestir um número de itens de cada tipo (ex.: 3 chapéus, 1 sobrancelha,
     1 cílio, 1 sapato de cada lado). O app lê esses limites do próprio Roblox (`avatar.roblox.com/v1/avatar-rules`,
     público, sem cookie, guardado por 6 h; sem resposta, usa só os limites conhecidos com certeza) e corta antes de
     enviar: item escolhido > peça do pacote de cabeça > peça de outro pacote > o que já estava; no empate, o mais
     recente. Um item escolhido que não coube aparece no resultado ("o Roblox não deixa vestir mais itens deste tipo
     juntos"); peças de pacote e itens antigos saem sem aviso, como nas partes únicas.
   - Se uma categoria só tinha itens que não puderam ser vestidos, ela fica como estava.
6. Se o Roblox ainda responder `LimitExceeded` (o caso da 0.6.1: um pacote "Base Body" e um pacote de cabeça, cada um
   com sua sobrancelha), o app tira a peça mais fraca do tipo que o Roblox apontou e veste de novo (até 3 vezes). Uma
   recusa que não dá para resolver aparece com o motivo em português, não com o JSON do Roblox.
7. Os itens que o Roblox recusou vestir aparecem no resultado.

Aplicar de novo é seguro: itens já possuídos não são pegos de novo, e a lista vestida é substituída, não somada.

## Grátis do Roblox

Um item só é tratado como grátis quando a busca oficial do catálogo diz que ele:

- é do criador Roblox;
- custa 0;
- está à venda;
- não é limitado (`Limited`/`LimitedUnique` ou com estoque).

**Busca:**
- `catalog.roblox.com/v2/search/items/details`, com o v1 como reserva, sem sessão;
- categorias 11/19, 3/3, 4/4 e 1/37, com `CreatorName=Roblox`, `MaxPrice=0`, `SalesTypeFilter=1`;
- os itens são separados nas categorias do editor pelo **tipo** do item, não pela subcategoria da busca.

**Pacotes:**
- as peças vêm de `catalog.roblox.com/v1/bundles/details`;
- os tipos das peças vêm de `catalog.roblox.com/v1/catalog/items/details` (com a sessão da conta aberta, que exige token CSRF).

O catálogo grátis fica guardado em `avatar-catalog.json`, na pasta de dados, por 24 h. São só dados públicos. **Atualizar catálogo** relê na hora.

## Privacidade e segurança

- O cookie só vai para `https://*.roblox.com`, no cabeçalho da própria conta. Buscas do catálogo e miniaturas não levam sessão.
- Miniaturas, a imagem do avatar e a lista de itens de cada conta ficam **só na memória**. Os avatares salvos ficam no cofre criptografado.
- Nada é feito sem clique. O log registra o tipo e o id de itens grátis pegos e o resultado, nunca cookies.
- Quando o Roblox troca o cookie durante as chamadas, o novo é salvo no cofre.

## Pendente de confirmação no Windows

Este ambiente de desenvolvimento não alcança o Roblox, então os formatos das respostas seguem a documentação
conhecida e testes com respostas fictícias (26 testes). Confira na primeira vez:

1. Se o catálogo carrega e mostra cabelos, roupas, cabeças e corpos grátis. Se mostrar "Catálogo indisponível", o
   motivo aparece na tela e o log (`%LocalAppData%\RbxSwitcher\app.log`) tem o status HTTP e os nomes dos campos.
2. Se **Corpo** e **Cabeça e rosto** têm itens. Pacotes dependem da busca 1/37 e de `bundles/details`.
3. Aplicar numa conta de teste com um cabelo grátis que ela não tem: deve aparecer "Itens grátis pegos".
4. Se roupas em camadas (camisa/calça 3D) vestem sem a ordem de camadas. Se o Roblox recusar, aparece no resultado.
