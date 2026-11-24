# Revisão de interface (0.4.1)

Feita com as skills `ui-design-review:ui-review` e `rbxswitcher-ui`. As telas foram vistas de verdade: o app
real, com contas fictícias, rodando no Wine (ver `tools/UiPreview/README.md`), em 1366×768 a 100 % e em
1920×1080 a 150 %. Comparações antes/depois: `dist/revisao-ui/` (não fazem parte do código).

## Problemas encontrados (por impacto) e o que mudou

| # | Problema (visto) | Mudança |
|---|---|---|
| 1 | Avatares: em 1366×768 os botões **Aplicar** e **Sortear tudo** ficavam abaixo da tela | Rodapé fixo na coluna da direita com Sortear/Limpar, Aplicar em @conta, Aplicar em várias e o resultado; imagem do avatar menor ao lado do seletor de conta; linhas da Seleção mais baixas |
| 2 | Contas: o cartão da conta quebrava números ("3.800 /" e "5.000" em duas linhas) e o **Plus · status** sumia | Números em linhas fixas que nunca quebram (reticências + dica se faltar espaço); rótulo "Pode enviar"; o chip do Plus voltou a aparecer |
| 3 | Avatares: catálogo indisponível aparecia como uma linha técnica cortada no rodapé ("busca 11/19: sem conexão…") | Aviso amarelo acima da grade, em palavras simples ("sem conexão com o Roblox"), com **Tentar de novo**; detalhes completos no log |
| 4 | Texto com pouco contraste (cinza #697081 = 3,6–3,9:1) em "—", "manter o atual", saldo antigo, "lido do Roblox em…" | Todo texto usa no mínimo o cinza secundário (6,6:1); borda de campos e caixas de marcar reforçada (≥ 3:1) |
| 5 | Saldo desatualizado indicado só por cor | A lista mostra "saldo de 3 h" (em palavras) na segunda linha |
| 6 | Teclado: chips, grade de itens, cadeados, tons de pele, avatares salvos e lista do lote não recebiam foco | Todos com Tab, foco visível, Enter/Espaço, setas na grade, nos tons e na lista; nomes acessíveis para leitor de tela |
| 7 | Símbolos de texto como ícones (◀ ▶ ⟳ ✕ ▾ e o emoji 🔒) — em fontes sem esses símbolos viravam "▯" | Ícones desenhados (voltar, avançar, recarregar, fechar, setas de menu) iguais em qualquer Windows/fonte; emoji removido da tela de bloqueio |
| 8 | Barra do navegador pesada: Início/Perfil/Inventário/Trades com o mesmo peso dos demais botões; foco inicial em "Início" parecia botão pressionado | Atalhos discretos (sem contorno), endereço no mesmo campo arredondado da busca, foco inicial na busca de contas |
| 9 | Plus: números cortados ("10.000 / 1…") com o painel de detalhes aberto; datas longas | Painel mais estreito (380–460 px), colunas com largura mínima para números inteiros, datas curtas ("Renova 11/12") |
| 10 | Plus: sem ordenação e sem totais | Clique no cabeçalho ordena (e inverte); linha de resumo: contas, com Plus (expirando), disponível hoje, leitura mais antiga |
| 11 | Plus (detalhes): histórico e origem dos limites em parágrafos técnicos sempre abertos | Uma linha simples ("Histórico completo lido em … · N envios nos últimos 30 dias") e **Detalhes técnicos** recolhido (origem dos números, leitura do histórico, diagnóstico) |
| 12 | Janela de lote: aviso cortado na borda direita, "Desmarcar" quebrando linha, barra de rolagem clara | Aviso medido e quebrado dentro da janela; Marcar visíveis/Desmarcar sob a lista; barras escuras pedidas ao Windows |
| 13 | Letras de 11 px em metadados | Mínimo de 12 px para texto; 11 px só nos números dentro dos chips |
| 14 | "grátis" em todos os itens; "Plus sem plus" na lista | Etiqueta só no que é exceção ("da conta"); "sem Plus" |
| 15 | Janela de abrir o Roblox com largura fixa em pixels e, ao abrir os detalhes, saindo pela borda da tela | Largura em unidades lógicas (acompanha 125/150 %); a janela sobe para caber inteira |

## Validação

- Prints antes/depois em 1366×768 (100 %) e 1920×1080 (150 %): Contas, Plus (lista, ordenada, detalhes de conta
  expirando e com limite reduzido), Avatares (sem catálogo e com catálogo), janela de lote, janela de abrir o Roblox
  com as opções abertas.
- 225 testes automatizados passando; build do app sem avisos.

## Ainda não confirmado (precisa do Windows real)

- **Barras de rolagem escuras:** o app pede o tema escuro ao Windows (`DarkMode_Explorer`); o Wine ignora, então
  nos prints elas aparecem claras.
- **Fonte Segoe UI:** os prints usam fontes substitutas; larguras reais podem variar alguns pixels. No Wine os
  dígitos às vezes aparecem riscados — artefato do Wine, não do app.
- **Leitor de tela (Narrador/NVDA):** os nomes acessíveis foram definidos, mas a leitura não foi ouvida.

## Revisão 0.6.1 — aba Distribuir

| # | Antes | Depois |
|---|---|---|
| 1 | Abas internas (Sugestões, Em andamento…) sem número: era preciso abrir cada uma para saber se havia algo | Contagem discreta ao lado do nome ("Em andamento 2"); some quando é zero |
| 2 | Etapas em andamento repetiam toda a lista de motivos, empurrando os botões para baixo | Motivos abertos só nas sugestões (onde se decide); nas etapas em andamento ficam em **Por que esta etapa**, recolhido |
| 3 | Textos com "(s)": "1 sugerida(s)", "2 cópia(s) prontas", "7 dia(s)", "4.2 venda(s)/dia" | Singular e plural certos ("1 sugerida", "2 cópias prontas", "7 dias", "4,2 vendas/dia", com vírgula decimal) |
| 4 | "Preço…." com ponto a mais na janela de configuração | Corrigido |

Validação: prints em 1366×768 (100 %) e 1920×1080 (150 %) das seis telas da aba Distribuir e da configuração;
261 testes passando.

## 0.6.9 — etapas com imagens

| Antes | Depois |
|---|---|
| Cada etapa listava os itens em texto ("envia: Black Headphones (líquido 2.240) + Small …") e o histórico técnico inteiro aberto | Os dois lados da trade como imagens dos itens (Sai de @origem ⇄ Sai de @destinatária), com nome, líquido ou "Small"; passar o mouse mostra nome completo, série e valor |
| "Líquido do lote" numa linha própria | No cabeçalho, discreto, junto do número da trade |
| Motivos e histórico sempre visíveis (ou em "Por que esta etapa") | Tudo em **Detalhes**, recolhido; avisos que pedem ação continuam à vista |

As imagens são as miniaturas públicas do Roblox (sem cookie), no mesmo cache da aba Avatares. Validação: prints em
1366×768 (100 %) e 1920×1080 (150 %).
