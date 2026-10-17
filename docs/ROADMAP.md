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
- [ ] Instalador (ver “A decidir”)
- [ ] Ajuste visual: linha escura de poucos pixels no topo do modo foco

## Próximas versões (propostas, a conversar)

- **0.2 — Conforto:** lembrar janelas destacadas e em qual monitor estavam; layouts salvos
  (ex.: “Noite”, “Garagem grande”); reordenar câmeras arrastando; iniciar com o Windows;
  ícone na bandeja.
- **0.3 — Áudio e PTZ:** ligar áudio por câmera; girar a câmera (ONVIF) quando suportado.
- **0.4 — Gravação local:** gravação manual e contínua com limite de espaço e apagamento
  automático; linha do tempo para rever.
- **0.5 — Eventos:** detecção de movimento com aviso do Windows e gravação por evento.
- **1.0 — Abertura do código:** licença, instalador assinado, i18n (pt-BR/en), site/README
  em inglês, contribuição.

## A decidir antes de seguir

1. **Licença** quando abrir o código (MIT, Apache-2.0 ou GPL-3.0). A LGPL do LibVLC é
   compatível com todas.
2. **Distribuição:** instalador MSIX (Microsoft Store/winget) ou Velopack (atualização
   automática), e **assinatura de código** (certificado pago ou Azure Trusted Signing).
3. **Descoberta automática** de câmeras na rede (ONVIF WS-Discovery) ou só cadastro manual.
4. **Gravação:** onde guardar, quanto espaço, formato (MP4 segmentado?), por quanto tempo.
5. **Acesso fora de casa:** fica fora do escopo (o app Tapo já cobre) ou entra no futuro
   com uma solução segura (ex.: VPN como Tailscale, nunca abrir portas)?
6. **Idioma** do código/README quando público (hoje: código em inglês, interface em pt-BR).

## Checklist obrigatório antes de tornar público

- [x] Repositório recriado do zero com o histórico limpo (2026-10-17); o antigo virou
      `sanfado/mirante-arquivo` (privado).
- [ ] **Apagar `sanfado/mirante-arquivo`**: ele ainda guarda os commits antigos com dados pessoais.
- [ ] Nunca reenviar o histórico antigo para este repositório; abrir o código é só mudar a
      visibilidade deste para público.
- [ ] Rodar a busca por dados pessoais (IPs da rede de casa, nomes, usuários de câmera) em
      todo o histórico e nas capturas de tela.
- [ ] Ativar *private vulnerability reporting* e *secret scanning* no repositório público.
