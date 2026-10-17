# Segurança

## Reportar uma vulnerabilidade

Enquanto o repositório for privado, fale direto com o mantenedor. Quando for público, use
**GitHub → Security → Report a vulnerability** (reporte privado). Não abra issue pública
para falhas de segurança.

## Modelo de ameaças (MVP)

O Mirante é um visualizador **local**: roda no PC do usuário, sem servidor, sem nuvem e sem
portas abertas. Ele só inicia conexões para as câmeras cadastradas.

| Ativo | Proteção |
|---|---|
| Senhas das câmeras | Gerenciador de Credenciais do Windows (DPAPI, por usuário, não sincroniza). Nunca em JSON, URL ou log. `CameraCredential.ToString()` mascara a senha. |
| Lista de câmeras (`%APPDATA%\Mirante\cameras.json`) | Sem segredos. Validada ao carregar: host, porta e caminho passam por lista de caracteres permitidos; entradas inválidas são descartadas. Gravação atômica; arquivo corrompido é guardado à parte. |
| Fotos (`%USERPROFILE%\Pictures\Mirante`) | Pasta local, de propósito fora da pasta “Imagens” que o OneDrive costuma sincronizar. |
| Motor de vídeo (LibVLC) | `--ignore-config` (não herda configurações do VLC instalado), `--keystore=memory` (não persiste credenciais), sem plugins Lua, sem diálogos de login. Credenciais passadas como opções da mídia, não na URL. |
| Cadeia de suprimentos | Versões centralizadas e travadas (`Directory.Packages.props` + `packages.lock.json`, restore em modo `--locked-mode` no CI), NuGet Audit em todo build, Dependabot, Actions fixadas por SHA. |
| Código | Análise de código `latest-recommended` com avisos tratados como erro. Executa sem privilégios de administrador. |

## Limitações conhecidas

- **RTSP não é criptografado.** Câmeras domésticas como as Tapo não oferecem RTSP sobre TLS:
  o vídeo trafega em claro na rede local. O login usa Digest (a senha não trafega em texto),
  mas quem estiver na mesma rede pode ver o vídeo. Recomendação: câmeras numa rede/VLAN
  separada e sem acesso externo (nada de redirecionar a porta 554 no roteador).
- Digest usa MD5 por exigência do protocolo (RFC 2617), apenas para autenticação.
- Ainda não há instalador assinado (code signing); planejado antes da primeira versão pública.
