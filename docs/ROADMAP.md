# Roadmap

## 0.1 — MVP (em andamento)

- [x] Cadastro de câmeras RTSP com teste de conexão (diferencia senha errada de câmera fora do ar)
- [x] Senhas no Gerenciador de Credenciais do Windows
- [x] Mosaico automático (adapta a monitores em pé e deitados)
- [x] Foco numa câmera (duplo-clique, teclas 1–9)
- [x] Destacar câmera em janela própria, no mesmo app
- [x] Tela cheia no monitor atual (janela principal e janelas destacadas)
- [x] Fotos em pasta local (fora do OneDrive)
- [x] Reconexão automática com espera crescente e diagnóstico
- [x] Tema escuro/claro/sistema, posição da janela lembrada
- [x] Uma instância só (abrir de novo traz a janela existente para frente)
- [ ] Testar com a câmera real da sala (login, reconexão ao reiniciar a câmera, queda de Wi-Fi)
- [x] Instalador local com Velopack (`build/pack.ps1`), sem assinatura de código ainda
- [x] Ajuste visual: linha escura de poucos pixels no topo do modo foco
- [x] Lembrar janelas destacadas (monitor, posição, tela cheia) e a tela cheia da janela principal
- [x] Ícone na bandeja (“manter na bandeja ao fechar”) e iniciar com o Windows
- [x] Atualização automática: baixa em segundo plano, instala só com clique (testado de 0.1.0 a 0.1.3 com feed local)
- [x] Workflow de release por tag (instalador + delta + GitHub Releases)
- [x] VLC enxuto: só os plugins usados (101 → 26 MB), sem componentes GPL desnecessários
- [x] Repositório público e atualização pelo GitHub funcionando para os usuários (2026-10-17)

## Feito depois da 0.1

- **0.2.0:** organizar a ordem das câmeras (mosaico e teclas 1–9).
- **0.2.1:** corrige o app fechando ao excluir uma câmera.
- **0.3.0:** volume até 200% e “Realçar voz”, salvos por câmera.

## Próximas versões (propostas, a conversar)

- **Layouts salvos** (ex.: “Noite”, “Garagem grande”) — conversar antes o que um layout guarda.
- **Controles da câmera (PTZ):** girar/inclinar câmeras como a Tapo C210 pelo app. Estudar:
  ONVIF (padrão, se a câmera expuser) ou o protocolo próprio da Tapo.
- **Falar pela câmera** (microfone do PC → alto-falante da câmera) e modo **“chamada”**
  (conversa contínua), como no app Tapo. Estudar: canal de áudio de volta do ONVIF/RTSP
  (*backchannel*) e se a Tapo o oferece fora do app dela; latência, eco e segurança.
- **Nivelador de áudio (compressor):** igualar sons baixos e altos. O VLC 3 não permite
  compressor e equalizador juntos por câmera; exige o app processar o áudio por conta própria.
- **Gravação local:** manual e contínua, com limite de espaço e apagamento automático;
  linha do tempo para rever.
- **Eventos:** detecção de movimento com aviso do Windows e gravação por evento.
- **Interface em inglês** (hoje só pt-BR; o README já é bilíngue).
- **1.0:** instalador assinado (SignPath), i18n, guia de contribuição.

## A decidir antes de seguir

1. ~~Licença~~ → **MIT** (decidido em 2026-10-17), com `THIRD-PARTY-NOTICES.md`.
2. **Assinatura de código** (certificado pago ou Azure Trusted Signing) — o instalador já é
   Velopack; sem assinatura o Windows mostra “editor desconhecido” na instalação.
3. **Descoberta automática** de câmeras na rede (ONVIF WS-Discovery) ou só cadastro manual.
4. **Gravação:** onde guardar, quanto espaço, formato (MP4 segmentado?), por quanto tempo.
5. **Acesso fora de casa:** fica fora do escopo (o app Tapo já cobre) ou entra no futuro
   com uma solução segura (ex.: VPN como Tailscale, nunca abrir portas)?
6. **Idioma** do código/README quando público (hoje: código em inglês, interface em pt-BR).

## Checklist para tornar público (concluído)

- [x] Repositório recriado do zero com o histórico limpo (2026-10-17); o antigo virou
      `sanfado/mirante-arquivo` (privado).
- [x] `sanfado/mirante-arquivo` apagado (2026-10-17).
- [x] Código aberto em 2026-10-17 (o histórico antigo nunca foi reenviado).
- [x] Busca por dados pessoais em todo o histórico, mensagens e imagens: nada encontrado (2026-10-17).
- [x] Reporte privado de vulnerabilidades, secret scanning com push protection e regras que
      protegem o histórico do `main` e as tags de versão (2026-10-17).
