<p align="center">
  <img src="src/Mirante.App/Assets/mirante-256.png" width="96" alt="Mirante" />
</p>

<h1 align="center">Mirante</h1>

<p align="center">
  Suas câmeras de casa, ao vivo e lado a lado, direto no PC.<br/>
  Sem nuvem, sem conta, sem telemetria.
</p>

<p align="center">
  <img src="docs/screenshots/mosaico.png" width="760" alt="Mosaico de câmeras (vídeos de demonstração)" />
</p>

> **Status:** MVP (0.1) em desenvolvimento, repositório privado. Ver [docs/ROADMAP.md](docs/ROADMAP.md).

## O que faz

- **Mosaico automático** que aproveita ao máximo a tela (num monitor em pé, as câmeras empilham).
- **Destacar câmera** em janela própria, no mesmo app, para pôr cada câmera num monitor.
- **Tela cheia no monitor onde a janela está** (F11), não no monitor principal.
- **Foco**: duplo-clique ou teclas `1`–`9` para ver uma câmera grande; `Esc` volta.
- **Fotos** (`F` ou botão da câmera) em `Imagens\Mirante` local, fora do OneDrive.
- **Reconexão automática** com diagnóstico: diferencia “senha recusada” de “câmera fora do ar”.
- **Teste de conexão** ao cadastrar: confirma IP, porta e senha antes de salvar.
- Decodificação na GPU (via LibVLC), interface Fluent do Windows 11 com tema escuro/claro.

Feito pensando nas câmeras **TP-Link Tapo** (RTSP `stream1`/`stream2`) e compatível com câmeras RTSP em geral.
A validação com câmeras reais ainda está em andamento (ver roadmap).

## Segurança e privacidade

- Senhas ficam no **Gerenciador de Credenciais do Windows** (DPAPI), nunca em arquivos do app.
- O endereço RTSP é montado **sem credenciais**; usuário e senha vão ao motor de vídeo por opções separadas e nunca aparecem em logs.
- LibVLC roda com `--ignore-config` e `--keystore=memory`: não lê configurações do VLC instalado nem grava senhas em disco. Scripts Lua do VLC não são distribuídos.
- Configuração validada ao carregar (um arquivo adulterado não injeta URLs arbitrárias); gravação atômica.
- Sem servidores, portas abertas, contas ou telemetria. O app só conversa com as câmeras que você cadastrou.
- Versões de pacotes travadas (`packages.lock.json`), auditoria de vulnerabilidades do NuGet no build e Dependabot.

Detalhes e limitações (RTSP não é criptografado) em [SECURITY.md](SECURITY.md).

## Atalhos

| Tecla | Ação |
|---|---|
| `F11` | Tela cheia no monitor atual |
| `Esc` | Sai da tela cheia / do foco |
| `1`–`9` / `0` | Foca a câmera N / volta ao mosaico |
| `F` | Foto da câmera sob o mouse |
| `Ctrl+N` | Adicionar câmera |
| Duplo-clique | Foca a câmera (na janela destacada: tela cheia) |

## Desenvolvimento

Requisitos: Windows 10/11 x64 e .NET SDK 10.

```bash
dotnet build
dotnet test --project tests/Mirante.Core.Tests
dotnet run --project src/Mirante.App
```

Modo demonstração (só em Debug), com vídeos locais no lugar das câmeras e capturas automáticas de tela:

```bash
dotnet run --project src/Mirante.App -- --demo <pasta-com-mp4> <pasta-das-capturas>
```

### Estrutura

```
src/Mirante.Core     Regras sem interface: câmeras, validação, RTSP (teste de login), credenciais, layout, persistência
src/Mirante.App      App WPF (WPF-UI + LibVLCSharp): telas, tiles de vídeo, reconexão
tests/               Testes (xUnit v3), incluindo câmera RTSP falsa e Gerenciador de Credenciais real
docs/                Roadmap, decisões de arquitetura (ADR) e capturas
```

Decisão de stack: [docs/adr/0001-stack.md](docs/adr/0001-stack.md).

## Licença

Ainda não definida (repositório privado). A decisão faz parte do roadmap antes de abrir o código.
