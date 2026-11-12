# Implementação: erros, sequência, frequência e espaço das poses

Guia complementar para o contrato e os fluxos do pacote `multi_xr`. Os exemplos são orientações de implementação; revise-os contra os tipos e APIs da versão Bun/websocket-sharp instalada antes de copiar. Nenhum código-fonte foi alterado nesta revisão.

## 1. Enviar `error` e aguardar o cliente, com timeout

Não existe garantia absoluta de entrega se a rede ou o processo cair. Bun documenta o retorno de `ServerWebSocket.send()` assim: `-1` significa enfileirado por backpressure, `0` significa descartado, e valor positivo indica bytes enviados. Mesmo o caso positivo não confirma que o cliente Unity processou a mensagem. O evento `drain` indica que a fila foi escrita para o socket; também não é ACK de aplicação.

Referência Bun: https://bun.sh/docs/runtime/http/websockets

Para erros fatais durante o `join`, o fluxo de menor complexidade é:

1. O servidor envia `error` e não chama `close()` imediatamente.
2. Se `send()` retornar `0`, a mensagem foi descartada: não há entrega a esperar; feche a conexão.
3. Se retornar `-1`, marque a conexão para fechar após `drain`; inicie o timeout de fechamento somente depois do `drain`.
4. Se retornar valor positivo, inicie um timeout de segurança, por exemplo 3 segundos.
5. O Unity trata o evento `ErrorReceived` e fecha o socket se ainda não recebeu `welcome`.
6. No `close` do servidor, cancele o timeout.

O cliente iniciar o fechamento após receber o erro evita que o servidor feche antes do processamento do Unity. O timer fecha conexões cujo cliente parou de responder. O timer é apenas uma salvaguarda, não uma garantia de entrega.

### Cliente Unity

Adicione um estado, por exemplo `private bool hasJoined;`. Defina `hasJoined = true` ao receber um `welcome` válido; redefina ao desconectar/iniciar uma nova conexão. No caso `error`, dispare primeiro o evento que mostra o erro e, se ainda não houve `welcome`, feche o cliente:

```csharp
case "error":
{
    ErrorMessage error = JsonUtility.FromJson<ErrorMessage>(json);
    ErrorReceived?.Invoke(error); // A UI/log recebe o motivo antes do fechamento.

    if (!hasJoined)
        CloseConnection();

    break;
}
```

Assim, erros não fatais de pose após o ingresso não fecham a sessão. A ação do evento é síncrona; se um consumidor precisar de trabalho assíncrono para exibir o erro, faça a UI guardar o estado antes do fechamento.

### Servidor Bun

Remova o `ws.close()` imediato das rejeições de `join`. Separe um helper para esse caso, deixando `sendError` simples para erros não fatais de pose:

```ts
type WSData = {
  // campos existentes...
  closeAfterError?: boolean;
  errorCloseTimer?: ReturnType<typeof setTimeout>;
};

function armErrorCloseTimeout(ws: ServerWebSocket<WSData>) {
  clearTimeout(ws.data.errorCloseTimer);
  ws.data.errorCloseTimer = setTimeout(() => {
    if (!ws.data.joined) ws.close(1008, "join rejected");
  }, 3000);
}

function rejectJoin(ws: ServerWebSocket<WSData>, code: string, message: string) {
  const result = ws.send(JSON.stringify({
    protocolVersion: 1,
    type: "error",
    code,
    message,
  }));

  if (result === 0) {
    ws.close(1011, "error send dropped");
    return;
  }

  if (result === -1) {
    ws.data.closeAfterError = true;
    return; // O handler drain arma o timer depois de a fila ser escrita.
  }

  armErrorCloseTimeout(ws);
}
```

No `websocket` handler, acrescente `drain` e limpe o timer em `close`:

```ts
drain(ws) {
  if (ws.data.closeAfterError) {
    ws.data.closeAfterError = false;
    armErrorCloseTimeout(ws);
  }
},
close(ws) {
  clearTimeout(ws.data.errorCloseTimer);
  // limpeza de participantes já existente...
},
```

Use `rejectJoin` para erros fatais das validações anteriores ao `welcome`. Para `INVALID_POSE` de um cliente já ingressado, envie `error` sem fechar automaticamente, conforme a política atual.

Para confirmação de que o aplicativo processou o erro, e não só para reduzir a chance de perda, adicione um ACK de aplicação. O servidor precisa marcar a conexão como rejeitada e aceitar `error_ack` nesse estado, apesar de não ter recebido `join`; feche após ACK ou timeout. Sem ACK, nunca afirme que o cliente recebeu/processou o erro.

## 2. Ignorar poses antigas

O `WebSocketSessionClient` recebe `player_pose` e invoca `PoseReceived`; é nesse recebimento que deve descartar sequência duplicada ou menor. A sequência é independente para cada `playerId`.

No `WebSocketSessionClient.cs`, adicione:

```csharp
private readonly Dictionary<string, int> lastSequenceByPlayer = new();
```

Inclua `using System.Collections.Generic;`. Substitua o case de `player_pose` por:

```csharp
case "player_pose":
{
    var pose = JsonUtility.FromJson<PlayerPoseMessage>(json);
    if (pose == null || string.IsNullOrEmpty(pose.playerId))
        break;

    if (lastSequenceByPlayer.TryGetValue(pose.playerId, out int lastSequence) &&
        pose.sequence <= lastSequence)
        break;

    lastSequenceByPlayer[pose.playerId] = pose.sequence;
    PoseReceived?.Invoke(pose);
    break;
}
```

No `player_left`, remova `lastSequenceByPlayer.Remove(left.playerId)`. Limpe o dicionário numa nova conexão. A sequência `1` é válida no começo de cada nova conexão porque o servidor gera um novo `playerId`.

TCP/WebSocket mantém a ordem dos frames de uma conexão; por isso esse filtro é defensivo e será mais útil caso o consumidor introduza filas paralelas, processamento assíncrono ou outro transporte. Sem o filtro, se uma sequência menor chegar ao consumidor depois da maior, ele de fato pode aplicar e exibir uma pose antiga.

## 3. Enviar quando a pose muda ou após 1 segundo

O `BunWebSocketClient` original usa:

- `playerSendHz = 20`: uma amostra a cada 50 ms.
- `posEps = 0.002 m` (2 mm), `rotEpsDeg = 0.5°`.
- Envio se algum transform ultrapassa o limite **ou** se passou 1 s do envio anterior.
- O loop usa `WaitForSeconds(1 / max(1, playerSendHz))`.

O `WebSocketSessionClient` atual já implementa a mesma condição em `PlayerSendLoop`: `HasPlayerPoseChanged(current) || Time.unscaledTime - timeSinceLastPlayerUpdate >= 1f`. A taxa de 20 Hz é de amostragem, não significa envio contínuo de 20 mensagens/s. Parado, há uma pose por segundo; em movimento, pode chegar a aproximadamente 20/s.

A comparação do pacote atual usa a pose já convertida para `NetworkOrigin`, distância com threshold ao quadrado e `Quaternion.Angle`. Isso evita problemas de wrap de `eulerAngles` perto de 0/360 graus, que o código original compara diretamente.

Mantenha o padrão do loop:

```csharp
var wait = new WaitForSeconds(1f / Mathf.Max(1f, playerSendHz));
while (IsConnected())
{
    if (!TryCapturePose(out PoseMessage current, out string error))
    {
        Debug.LogError(error);
        sendLoopPlayer = null;
        yield break;
    }

    bool changed = HasPlayerPoseChanged(current);
    bool heartbeatDue = Time.unscaledTime - timeSinceLastPlayerUpdate >= 1f;
    if (changed || heartbeatDue) // lastSentPose null torna a pose inicial uma mudança
    {
        current.sequence = ++poseSequence;
        ws.Send(JsonUtility.ToJson(current));
        lastSentPose = current;
        timeSinceLastPlayerUpdate = Time.unscaledTime;
    }

    yield return wait;
}
```

O projeto atual envia a primeira pose imediatamente, pois `lastSentPose` começa nulo. O original chama `PrimeCacheStates()` antes do loop, então a primeira pose imóvel normalmente é enviada no heartbeat de 1 s. Manter o envio inicial imediato é preferível para criar o avatar remoto sem atraso. Se `Time.timeScale` puder ser zero e ainda for necessário enviar poses, use `WaitForSecondsRealtime` junto de `Time.unscaledTime`.

## 4. Configurar um `NetworkOrigin` estável

1. Na cena, crie um GameObject vazio chamado `NetworkOrigin` como raiz ou sob pais sem escala.
2. Não o coloque como filho do `XR Origin`, da câmera ou dos controladores. Não mova/rotacione o `NetworkOrigin` quando o jogador se locomover.
3. Para a primeira cena compartilhada, use posição `(0,0,0)`, rotação identidade e escala `(1,1,1)` nos clientes. Se o mundo usar outro marco, escolha o mesmo marco lógico, orientação e escala em todos.
4. Arraste a referência para o campo `networkOrigin` do `WebSocketSessionClient`.
5. Continue usando o `XR Origin` para tracking e locomoção local. `root`, cabeça e mãos são os transforms que se movem; converta-os para o espaço relativo do `NetworkOrigin` ao enviar.
6. Ao receber uma pose, o consumidor do evento precisa converter de volta para coordenadas de mundo antes de chamar `SetPositionAndRotation`.

O código atual usa `InverseTransformPoint`, que aplica escala. Portanto, exija escala unitária no `NetworkOrigin`: caso contrário, a coordenada relativa deixa de representar metros. Se não quiser aplicar escala, use explicitamente:

```csharp
Vector3 relativePosition = Quaternion.Inverse(networkOrigin.rotation) *
                           (target.position - networkOrigin.position);
Quaternion relativeRotation = Quaternion.Inverse(networkOrigin.rotation) * target.rotation;

Vector3 worldPosition = networkOrigin.position + networkOrigin.rotation * relativePosition;
Quaternion worldRotation = networkOrigin.rotation * relativeRotation;
```

Com escala unitária, essas expressões equivalem ao cálculo atual para posições/rotações. A escala unitária continua sendo a regra mais simples para o protocolo.

## Arquivos relacionados

- `Assets/Plugins/CloseSheepNetwork/Script/BunWebSocketClient.cs` — frequência e comparação de pose do projeto original.
- `Packages/multi_xr/Runtime/WebSocket/WebSocketSessionClient.cs` — loop e captura de pose do pacote.
- `Packages/multi_xr/Runtime/Core/protocol/PlayerPoseMessage.cs` — estrutura recebida e ID por participante.
- `Packages/multi_xr/Documentation~/protocol-v1.md` — contrato que deve descrever campos, códigos, sequência e unidades.
