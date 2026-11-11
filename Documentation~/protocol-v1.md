# Protocolo WebSocket v1

## Escopo

O protocolo conecta até cinco participantes na mesma sala:

- `quest_player`: envia sua pose e recebe as poses dos demais.
- `desktop_viewer`: recebe poses, mas não publica avatar.

O servidor Bun retransmite as mensagens para os clientes da mesma sala. Nesta versão, a prioridade é testar na rede local. Todos os dispositivos devem conectar ao IP do computador que está executando o servidor, na porta TCP `7777`.

## Regras dos dados de pose

- Posições são expressas em metros.
- Rotações são quaternions no formato `{ "x": 0, "y": 0, "z": 0, "w": 1 }`.
- As poses são relativas ao mesmo objeto `NetworkOrigin`, presente na mesma posição, rotação e escala em todos os clientes.
- Cada pose contém `position` e `rotation`.
- `root` posiciona o corpo do avatar.
- `head`, `leftHand` e `rightHand` orientam a cabeça e as mãos. O corpo pode ser animado por IK no cliente que recebe as poses.
- Mãos podem ser `null` quando o tracking correspondente não estiver disponível.
- `sequence` aumenta a cada pose enviada pelo mesmo participante. O cliente pode ignorar poses com sequência anterior à última recebida.

## Cliente → servidor: `join`

Enviado uma vez, logo após abrir a conexão WebSocket.

```json
{
  "protocolVersion": 1,
  "type": "join",
  "roomId": "sala-local",
  "clientId": "quest-a-001",
  "role": "quest_player",
  "displayName": "Quest A"
}
```

`role` deve ser `quest_player` ou `desktop_viewer`. O servidor deve rejeitar versões incompatíveis, salas cheias e papéis não aceitos.

## Servidor → cliente: `welcome`

Confirma a entrada e fornece a lista dos participantes que já estão na sala. Isso permite que um participante que chegou depois descubra quem já está conectado.

```json
{
  "protocolVersion": 1,
  "type": "welcome",
  "sessionId": "sala-local",
  "selfId": "player-001",
  "role": "quest_player",
  "players": [
    {
      "playerId": "player-002",
      "role": "quest_player",
      "displayName": "Quest B"
    }
  ]
}
```

O servidor atribui `selfId` e associa esse identificador à conexão. O cliente não escolhe o `playerId` usado nas mensagens de pose.

## Quest → servidor: `pose`

Enviado somente por participantes com papel `quest_player`. A frequência inicial sugerida é de 15 mensagens por segundo.

```json
{
  "protocolVersion": 1,
  "type": "pose",
  "sequence": 42,
  "root": {
    "position": { "x": 1.2, "y": 0.0, "z": -0.4 },
    "rotation": { "x": 0.0, "y": 0.7, "z": 0.0, "w": 0.7 }
  },
  "head": {
    "position": { "x": 1.2, "y": 1.65, "z": -0.4 },
    "rotation": { "x": 0.0, "y": 0.7, "z": 0.0, "w": 0.7 }
  },
  "leftHand": null,
  "rightHand": {
    "position": { "x": 1.5, "y": 1.2, "z": -0.2 },
    "rotation": { "x": 0.0, "y": 0.0, "z": 0.0, "w": 1.0 }
  }
}
```

O servidor identifica o remetente pela conexão. Não deve aceitar um `playerId` arbitrário no corpo da mensagem.

## Servidor → participantes: `player_pose`

O servidor inclui o ID associado à conexão e retransmite a pose aos demais participantes da sala. O cliente que enviou a pose não precisa recebê-la de volta.

```json
{
  "protocolVersion": 1,
  "type": "player_pose",
  "playerId": "player-001",
  "sequence": 42,
  "root": {
    "position": { "x": 1.2, "y": 0.0, "z": -0.4 },
    "rotation": { "x": 0.0, "y": 0.7, "z": 0.0, "w": 0.7 }
  },
  "head": {
    "position": { "x": 1.2, "y": 1.65, "z": -0.4 },
    "rotation": { "x": 0.0, "y": 0.7, "z": 0.0, "w": 0.7 }
  },
  "leftHand": null,
  "rightHand": {
    "position": { "x": 1.5, "y": 1.2, "z": -0.2 },
    "rotation": { "x": 0.0, "y": 0.0, "z": 0.0, "w": 1.0 }
  }
}
```

## Servidor → clientes: presença e erros

Quando alguém entra:

```json
{
  "protocolVersion": 1,
  "type": "player_joined",
  "playerId": "player-001",
  "role": "quest_player",
  "displayName": "Quest A"
}
```

Quando alguém sai ou perde a conexão:

```json
{
  "protocolVersion": 1,
  "type": "player_left",
  "playerId": "player-001",
  "reason": "disconnected"
}
```

Quando o servidor rejeita uma entrada:

```json
{
  "protocolVersion": 1,
  "type": "error",
  "code": "ROOM_FULL",
  "message": "A sala já tem cinco participantes."
}
```

Códigos iniciais: `ROOM_FULL`, `PROTOCOL_VERSION_UNSUPPORTED`, `INVALID_ROLE`, `INVALID_MESSAGE`, `INVALID_POSE`, `ROOM_NOT_FOUND`, `INVALID_NAME`, `UNKNOWN_MESSAGE_ERROR`.

## Comportamento do servidor

1. Aceita até cinco conexões na sala.
2. Recebe `join` e valida a versão, sala e papel.
3. Responde `welcome` com os participantes existentes.
4. Avisa os participantes existentes com `player_joined`.
5. Aceita `pose` somente de `quest_player`.
6. Valida os campos e retransmite como `player_pose` aos demais participantes.
7. Ao desconectar, envia `player_left` e remove o participante da sala.

## Fora do escopo desta versão

Voz, objetos sincronizados, interações, movimento de objetos, descoberta pela Internet e autenticação de produção. O beacon UDP `47777` pode ajudar a encontrar o servidor na LAN, mas não é necessário quando o IP do servidor é informado diretamente.