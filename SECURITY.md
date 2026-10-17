# Segurança

## Reportar uma vulnerabilidade

Enquanto o repositório for privado, fale direto com o mantenedor. Quando for público, use
**GitHub → Security → Report a vulnerability** (reporte privado). Não abra issue pública
para falhas de segurança.

## Modelo de ameaças (MVP)

O Mirante é um visualizador **local**: roda no PC do usuário, sem servidor, sem nuvem e sem
portas abertas. Ele só inicia conexões para as câmeras cadastradas e, se permitido, para o
GitHub (procurar versões novas do próprio app; nenhum dado do usuário é enviado).

| Ativo | Proteção |
|---|---|
| Senhas das câmeras | Gerenciador de Credenciais do Windows (DPAPI, por usuário, não sincroniza). Nunca em JSON, URL ou log. `CameraCredential.ToString()` mascara a senha. |
| Lista de câmeras (`%APPDATA%\Mirante\cameras.json`) | Sem segredos. Validada ao carregar: host, porta e caminho passam por lista de caracteres permitidos; entradas inválidas são descartadas. Gravação atômica; arquivo corrompido é guardado à parte. |
| Fotos (`%USERPROFILE%\Pictures\Mirante`) | Pasta local, de propósito fora da pasta “Imagens” que o OneDrive costuma sincronizar. |
| Motor de vídeo (LibVLC) | `--ignore-config` (não herda configurações do VLC instalado), `--keystore=memory` (não persiste credenciais), só os plugins necessários (lista de permitidos no `.csproj`: ~50 de ~330, sem Lua, rede de saída, codificadores ou cofre de senhas em arquivo), sem diálogos de login. Credenciais passadas como opções da mídia, não na URL. |
| Cadeia de suprimentos | Versões centralizadas e travadas (`Directory.Packages.props` + `packages.lock.json`, restore em modo `--locked-mode` no CI), NuGet Audit em todo build, Dependabot, Actions fixadas por SHA. |
| Instalação | Por usuário, sem administrador (`%LOCALAPPDATA%\Mirante`). “Iniciar com o Windows” usa só a chave `Run` do usuário. A desinstalação remove essa chave e apaga as senhas do Gerenciador de Credenciais. |
| Atualizações | Só do GitHub Releases deste repositório, por HTTPS; cada pacote é conferido pelo hash publicado no feed do release. Baixadas em segundo plano, **instaladas só com o clique do usuário**. A consulta pode ser desligada nas Configurações. Release feito pelo CI a partir de tag, com permissão de escrita só nesse job. |
| Código | Análise de código `latest-recommended` com avisos tratados como erro. Executa sem privilégios de administrador. |

## Limitações conhecidas

- **RTSP não é criptografado.** Câmeras domésticas como as Tapo não oferecem RTSP sobre TLS:
  o vídeo trafega em claro na rede local. O login usa Digest (a senha não trafega em texto),
  mas quem estiver na mesma rede pode ver o vídeo. Recomendação: câmeras numa rede/VLAN
  separada e sem acesso externo (nada de redirecionar a porta 554 no roteador).
- Digest usa MD5 por exigência do protocolo (RFC 2617), apenas para autenticação.
- Ainda não há instalador assinado (code signing); planejado antes da primeira versão pública.
  Até lá, a integridade das atualizações depende da conta do GitHub do mantenedor (use 2FA).
