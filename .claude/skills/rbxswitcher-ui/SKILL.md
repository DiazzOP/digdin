---
name: rbxswitcher-ui
description: Auditoria e melhoria visual do aplicativo desktop RbxSwitcher. Use ao redesenhar telas, componentes, sidebar de contas, painel Roblox Plus, filtros, acessibilidade, estados de sessão e validação visual sem alterar a lógica de autenticação.
---

# RbxSwitcher UI — Skill de design e validação

## Missão
Elevar o RbxSwitcher à qualidade visual de um aplicativo desktop profissional para gerenciar dezenas ou centenas de contas Roblox. Priorize legibilidade, navegação rápida, consistência visual e produtividade. Preserve completamente as funcionalidades e os limites de segurança existentes.

## Passo inicial obrigatório
1. Inspecione o repositório e identifique a stack real (ex.: PySide6/PyQt, WPF/WinUI, Electron/Tauri ou WebView). Não presuma que o frontend usa HTML/CSS.
2. Localize componentes, estilos, recursos, gerenciamento de sessão, motor do navegador integrado e testes.
3. Analise as screenshots fornecidas pelo usuário e descreva os problemas observáveis antes de alterar o código.
4. Faça um plano curto e implemente por áreas pequenas, com commits ou checkpoints reversíveis.

## Direção visual
- Tema escuro premium, com hierarquia clara e poucos elementos decorativos.
- Tokens de referência (ajustáveis depois de verificar o código): background #0B1020, surface #151C2D, surface-hover #1D2940, primary #3B82F6, text #F1F5F9, muted #94A3B8, success #22C55E, warning #F59E0B, danger #EF4444.
- Use uma escala consistente de espaçamento (4, 8, 12, 16, 24, 32 px); bordas, raios, sombras e ícones coerentes.
- Evite fonte minúscula e blocos de texto técnico em excesso; priorize 12–14 px para metadados e 14–16 px para texto de interface, respeitando escala do SO.
- Não substitua os dados reais por valores fictícios. Diferencie claramente dados confirmados, estimados, desatualizados, pendentes e indisponíveis.

## Organização recomendada
### Aba CONTAS
- Sidebar redimensionável, com busca, grupos, filtros, avatar, identificador @, status da sessão e dados resumidos.
- Seleção atual muito clara, estados hover/foco visíveis e lista virtualizada quando necessário.
- Mantenha a navegação Roblox com a maior área útil possível. Evite alterar a UI do site Roblox dentro do navegador.
- Detalhes técnicos devem ser recolhíveis e não cobrir o navegador.

### Aba PLUS
- Reaproveite filtros e padrões da aba CONTAS, com resumo consolidado de todas as contas (ou do subconjunto selecionado).
- Prefira composição vertical: cabeçalho -> indicadores -> filtros -> lista/tabela de contas -> detalhe de conta.
- Indicadores: assinaturas ativas, vencimentos próximos, limite diário disponível, limite de 30 dias, pendências, atualização dos dados.
- Permita busca, filtro por grupo, assinatura/status, ordenação, seleção múltipla e densidade compacta.
- Exiba detalhes individuais ao selecionar uma conta; não duplique textos longos em toda a lista.
- Em totais, indique quais contas foram incluídas e quando os dados foram consultados.

## Estados e responsividade
- Cobrir loading, vazio, erro, sessão expirada, sem conexão, dados parciais, sem assinatura e ação desabilitada.
- Testar em 1366x768 e 1920x1080, DPI 100%, 125% e 150%, e redimensionamento da janela.
- Não permitir textos cortados, botões inacessíveis, sobreposições nem barras desnecessárias.
- As interações devem funcionar por mouse e teclado, com ordem de foco e rótulos adequados.

## Segurança e preservação funcional
- Não modificar autenticação, cookies, tickets, armazenamento, perfis WebView, isolamento entre contas ou fluxo de transferências sem instrução explícita.
- Nunca logar, exibir ou compartilhar credenciais, tokens ou cookies.
- Não automatizar envio de Robux.
- Não instalar dependências ou executar comandos externos não confiáveis sem revisar o impacto.

## Ciclo de revisão visual
1. Inspecionar a UI atual e reunir uma screenshot antes da alteração.
2. Propor wireframe e tokens de design.
3. Implementar um único conjunto coerente de mudanças.
4. Compilar, iniciar e executar o aplicativo real com dados não sensíveis.
5. Capturar screenshot após a alteração com ferramenta apropriada à stack; comparar alinhamento, contraste, densidade, dimensões e estados.
6. Testar alternância de contas, filtros, Plus, navegador e responsividade; registrar resultados e problemas remanescentes.
7. Não declarar 'finalizado' sem validação funcional e visual. Quando ferramentas visuais não estiverem disponíveis, declarar claramente a limitação.

## Formato de entrega
- Diagnóstico visual (3–8 problemas ordenados por impacto)
- Mudanças implementadas e arquivos afetados
- Evidências de validação (telas, resoluções e testes)
- Problemas ainda em aberto e próximos passos
