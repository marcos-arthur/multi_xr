# Protocolo WebSocket v1

Este documento descreve as mensagens atualmente implementadas em `multi_xr` e `multi_xr_server`. Mensagens são objetos JSON enviados em frames WebSocket. Cliente e servidor devem usar `protocolVersion: 1`.

## Sessão e identidade

- O servidor atual aceita a sala `sala-local` e até 10 participantes ativos (`quest_player` e `desktop_viewer`, somados). Sessões temporárias `identity_lobby` têm limite separado de 20 conexões e não ocupam vaga ativa.
- `role` aceita `quest_player`, `desktop_viewer` e `identity_lobby`. A sessão de identidade recebe roster e reservas, mas não envia poses nem mensagens Pointer.
- `displayName` é texto não vazio, limitado a 32 caracteres.
- `displayColor` é uma string RGB hexadecimal com seis dígitos, sem `#` (`RRGGBB`), por exemplo `FF8800`. O cliente Unity envia esse formato por `ColorUtility.ToHtmlStringRGB`; o servidor rejeita formatos inválidos.
- Não há `clientId` escolhido pelo cliente: o servidor atribui `playerId`/`selfId`.
- Não há timestamp de envio nem frequência negociada no protocolo v1.

## Mensagens

### Cliente → servidor: `join`

```json
{
  "protocolVersion": 1,
  "type": "join",
  "roomId": "sala-local",
  "role": "quest_player",
  "displayName": "Quest A",
  "displayColor": "FF8800",
  "reservationToken": "token-opcional-de-32-hex"
}
```

`reservationToken` é opcional e usado pelo fluxo do Cave-visualization para transferir ao jogador a reserva temporária de cor obtida na cena de identidade. O servidor valida a cor no ingresso e rejeita cores ativas ou reservadas por outra sessão.

### Servidor → cliente: `welcome`

Confirma o ingresso e lista quem já estava na sala. O `displayColor` de cada participante segue o formato `RRGGBB`.

```json
{
  "protocolVersion": 1,
  "type": "welcome",
  "sessionId": "sala-local",
  "selfId": "id-atribuido-pelo-servidor",
  "role": "quest_player",
  "players": [
    {
      "playerId": "outro-id",
      "role": "quest_player",
      "displayName": "Quest B",
      "displayColor": "33AAFF"
    }
  ]
}
```

O jogador que acabou de entrar não está na sua própria lista `players`. O `welcome` contém o papel local, mas a cor local não é ecoada.

### Servidor → clientes existentes: `player_joined`

```json
{
  "protocolVersion": 1,
  "type": "player_joined",
  "playerId": "id-atribuido-pelo-servidor",
  "role": "quest_player",
  "displayName": "Quest A",
  "displayColor": "FF8800"
}
```

### Cliente `quest_player` → servidor: `player_pose`

```json
{
  "protocolVersion": 1,
  "type": "player_pose",
  "sequence": 42,
  "root": {
    "position": { "x": 0.0, "y": 0.0, "z": 0.0 },
    "rotation": { "x": 0.0, "y": 0.0, "z": 0.0, "w": 1.0 }
  },
  "head": {
    "position": { "x": 0.0, "y": 1.65, "z": 0.0 },
    "rotation": { "x": 0.0, "y": 0.0, "z": 0.0, "w": 1.0 }
  },
  "leftHand": null,
  "rightHand": null
}
```

Root e cabeça são obrigatórios; cada pose é composta por posição `{x,y,z}` e quaternion `{x,y,z,w}`. Mãos podem ser `null` quando indisponíveis. Valores numéricos devem ser finitos e o quaternion não pode ser nulo. Posições e rotações são relativas ao `NetworkOrigin`; os participantes precisam compartilhar o mesmo referencial lógico e usar origem com escala efetiva unitária.

O `sequence` é inteiro crescente por jogador. O servidor retransmite a pose aos outros clientes com `playerId` atribuído e campos de protocolo. O receptor Unity descarta poses cuja sequência não seja maior que a última aceita daquele jogador. O contador é reiniciado ao abrir uma nova conexão.

`playerSendHz` controla a frequência de amostragem máxima no cliente Unity (padrão 20 Hz), não é um campo negociado. A pose é enviada quando posição/rotação ultrapassa os thresholds configurados ou após um segundo sem envio. `desktop_viewer` não publica poses.

### Servidor → clientes: `player_left`

```json
{
  "protocolVersion": 1,
  "type": "player_left",
  "playerId": "id-atribuido-pelo-servidor",
  "reason": "disconnected"
}
```

`reason` informa o motivo fornecido pelo servidor, por exemplo `disconnected`.

### Servidor → cliente: `error`

```json
{
  "protocolVersion": 1,
  "type": "error",
  "code": "INVALID_COLOR",
  "message": "Display color must be six hexadecimal RGB characters (RRGGBB), without '#'."
}
```

O servidor usa códigos como `INVALID_MESSAGE`, `PROTOCOL_VERSION_UNSUPPORTED`, `ROOM_NOT_FOUND`, `INVALID_ROLE`, `INVALID_NAME`, `INVALID_COLOR`, `ROOM_FULL`, `INVALID_POSE` e `UNKNOWN_MESSAGE_ERROR`. O campo `message` descreve a rejeição. Em rejeição ao join, o cliente Unity notifica `ErrorReceived` e fecha a conexão.

## Compatibilidade

`displayColor` é obrigatório no `join` e nos dados de participante usados no roster/evento de entrada. Clientes e servidores anteriores que não implementem esse campo devem ser atualizados juntos. O servidor não transmite no `welcome` os dados de aparência do próprio jogador, pois o cliente já os configurou localmente.

## Mensagens de aplicação (`app`)

Após o `join`, o servidor aceita `app` de `quest_player`, `desktop_viewer` e `identity_lobby`, com restrições por canal descritas abaixo. O envelope completo tem limite de 64 KiB em UTF-8; o servidor limita cada origem a 240 mensagens e 2 MiB por janela de um segundo. O envio sem `to` vai aos outros participantes permitidos; `to` precisa identificar outro participante da mesma sala. O servidor injeta `playerId` no envelope de saída e não retransmite ao emissor. O cliente só considera `SendAppMessage` aceito quando entrou na fila local, sem confirmação de entrega.

O canal `pointer` é aceito por `quest_player` e `desktop_viewer`; o payload contém JSON PointerWire v1 e o servidor valida tipo, campos, limites e autoria dos objetos. O canal `identity` é restrito a `identity_lobby` e recebe comandos JSON v1: `color_list_request` (token opcional), `color_reserve` (`color` em `RRGGBB`, token GUID N e nome), e `color_release`. Reservas duram 120 segundos e são renovadas pelo lobby; a lista pública informa cor, nome e expiração, enquanto a listagem individual pode incluir o token da própria reserva. O servidor valida reservas e transferência no `join` de `quest_player`. Sessões identity_lobby não recebem eventos de pose nem consomem vagas ativas.

Para Pointer, remoção só pode atingir objetos registrados para o autor, e `strokes_clear`/`measures_clear` globais são rejeitados. IDs de objetos são mantidos em memória durante a vida do processo; não existe persistência quando todos saem ou o servidor reinicia. Snapshots direcionados usam `board_request` e `board_snapshot`, divididos em partes por traço/medida; as partes respeitam 28 KiB no Pointer para manter margem dentro do limite externo. O novo participante bufferiza alterações ao vivo até concluir snapshot, com teto de 128 mensagens/256 KiB; exceder o teto encerra a conexão para evitar estado silenciosamente divergente.

Erros comuns incluem `UNKNOWN_MESSAGE_TYPE`, `UNKNOWN_APP_CHANNEL`, `APP_MESSAGE_TOO_LARGE`, `APP_RATE_LIMITED`, `INVALID_RECIPIENT`, `INVALID_POINTER_MESSAGE`, `POINTER_NOT_OWNED`, `POINTER_AUTHOR_MISMATCH`, `GLOBAL_CLEAR_FORBIDDEN` e `POINTER_CAPACITY_REACHED`. Três mensagens inválidas fecham a conexão. Sob backpressure, a conexão lenta é encerrada; o remetente não recebe confirmação de aplicação.
