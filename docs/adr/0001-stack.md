# ADR 0001 — Stack: .NET 10 + WPF + WPF-UI + LibVLCSharp

- **Status:** aceita (2026-10-17)

## Contexto

Visualizador de câmeras RTSP para o PC (Windows), com várias câmeras 2K ao mesmo tempo,
vários monitores, visual premium e foco em segurança. O protótipo em Python + Tk
decodificava com ffmpeg e passava cada quadro pelo Python: inviável para várias câmeras.

## Opções avaliadas

| Opção | Prós | Contras |
|---|---|---|
| **C# .NET 10 + WPF + LibVLCSharp** | Decodificação na GPU, motor de vídeo maduro (VLC já funcionava com a câmera), executável nativo, acesso direto a APIs do Windows (Gerenciador de Credenciais, monitores, DPI) | Só Windows |
| Python + Qt (PySide6) + mpv | Multiplataforma, rápido de iterar | Distribuição pesada (~150 MB), empacotamento Python costuma disparar antivírus |
| Tauri (Rust) + go2rtc | UI web, WebRTC, caminho para acesso pelo navegador | Servidor local extra rodando (mais superfície de ataque), mais peças para manter |

## Decisão

.NET 10 (LTS) + WPF, com:

- **WPF-UI** (MIT): controles e janelas Fluent do Windows 11 (Mica, tema claro/escuro).
- **LibVLCSharp + LibVLC 3** (LGPL 2.1): RTSP, aceleração por hardware, fotos.
- **CommunityToolkit.Mvvm** (MIT): MVVM com geradores de código.
- **xUnit v3** para testes.

O código de regras (`Mirante.Core`) não depende de WPF nem do LibVLC, para permitir trocar
a interface no futuro (por exemplo, Avalonia para Linux/macOS). O que é específico do
Windows fica isolado em `Mirante.Core/Platform/Windows`.

## Consequências

- O vídeo do LibVLC é uma janela nativa (HWND) que fica **por cima** de qualquer conteúdo WPF
  (“airspace”). Por isso:
  - tudo que aparece sobre o vídeo (nome, status, botões) é conteúdo do `VideoView`, que o
    LibVLCSharp desenha numa janela transparente sobreposta;
  - diálogos são janelas separadas, e não pop-ups dentro da janela principal;
  - avisos (“foto salva”) ficam na barra de título, fora da área de vídeo.
- A sobreposição não acompanha a visibilidade do WPF sozinha: o tile esconde o próprio
  conteúdo quando fica invisível (modo foco / janela destacada).
- Mover uma câmera entre janelas reinicia o player (≈1 s), porque o LibVLC 3 não troca de
  janela durante a reprodução.
- LGPL do LibVLC: compatível com distribuição aberta ou fechada, desde que as DLLs do VLC
  sejam distribuídas como bibliotecas dinâmicas substituíveis (é o caso).
