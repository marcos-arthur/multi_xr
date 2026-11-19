# API e guia de integração

Este documento descreve a API pública disponível para um projeto consumidor do pacote `multi_xr`. Os campos serializados no Inspector são configuráveis no Unity; a API C# expõe propriedades somente para leitura, eventos e métodos públicos.

## WebSocketSessionClient

Namespace: `com.imd.ackit.multi_xr.CustomWebSocket`

Adicione o componente a um GameObject da cena. A conexão e o envio do `join` começam automaticamente em `Start`; não há método público `Connect` nem setters públicos para reconfigurar o componente em runtime.

### Configurações do Inspector

| Campo | Padrão | Uso |
|---|---:|---|
| `Server Ip` | `192.168.0.10` | Endereço do servidor WebSocket. |
| `Server Port` | `7777` | Porta, atualmente configurada como texto. |
| `Max Messages Per Frame` | `20` | Limite por frame para mensagens de sessão e poses. |
| `Max App Messages Per Frame` | `10` | Limite independente por frame para mensagens `app`. |
| `Room Id` | `sala-local` | Sala solicitada no `join`. |
| `Role` | `quest_player` | Papel enviado ao servidor: `quest_player`, `desktop_viewer` ou `identity_lobby` (uso interno da cena de identidade). |
| `Display Name` | `Quest A` | Nome enviado no `join`. |
| `Reservation Token` | vazio | Token opcional para reclamar uma reserva temporária de cor no `join`. |
| `Display Color` | padrão do Unity | Cor convertida para seis dígitos hexadecimais RGB sem `#` no protocolo (`RRGGBB`). |
| `Player Send Hz` | `20` | Frequência máxima de amostragem/envio de poses. |
| `Network Origin` | sem referência | Referencial compartilhado para converter poses locais e remotas. Deve permanecer estável e ter escala efetiva `(1,1,1)`. |
| `Root`, `Head` | sem referência | Transforms locais usados na captura da pose; root e head são necessários para enviar poses válidas. (PS.: Ainda em desenvolvimento, então em caso do root não existir no modelo, setar para o XR Origin ou qualquer outro gameobject estático da cena.) |
| `Left Hand`, `Right Hand` | sem referência | Transforms opcionais das mãos. |
| `Position Threshold` | `0.002` | Deslocamento mínimo (metros) para considerar que a pose mudou. |
| `Rotation Threshold Degrees` | `0.5` | Rotação mínima (graus) para considerar que a pose mudou. |

O cliente amostra conforme `Player Send Hz`, mas transmite quando a pose ultrapassa um dos limites ou quando passa um segundo sem envio (heartbeat). A sequência é incrementada nos envios e poses recebidas com sequência repetida ou anterior são descartadas por jogador. Os papéis `desktop_viewer` e `identity_lobby` não iniciam envio de poses.

### Propriedades públicas

Todas são somente leitura:

```csharp
Transform NetworkOrigin { get; }
string ServerIp { get; }
string ServerPort { get; }
string RoomId { get; }
string Role { get; }
string DisplayName { get; }
Color DisplayColor { get; }
string ReservationToken { get; }
```

### Métodos públicos

```csharp
bool IsConnected();
void CloseConnection();
bool SendAppMessage(string channel, string payload, string to = null);
```

`IsConnected` informa se o socket está aberto. `CloseConnection` inicia o fechamento da conexão e encerra os loops de envio. Para mudar endereço, sala, papel, nome ou cor, configure os campos no Inspector antes de iniciar o componente.

`SendAppMessage` deve ser chamado na thread principal da Unity. `channel` aceita `pointer` ou `identity`; o servidor restringe `identity` a `identity_lobby`, e Pointer não deve ser enviado por esse papel. `payload` é texto opaco para o pacote. `to` nulo ou vazio transmite aos demais participantes; um id direciona a mensagem a um participante, que o servidor ainda precisa validar. O método retorna `true` quando o envelope foi aceito pela fila local, não quando foi confirmado pelo servidor.

O cliente limita cada envelope serializado a 64 KiB UTF-8, e a fila de saída a 128 KiB e 256 mensagens, contando o envio em andamento. Se estiver desconectado, os argumentos forem inválidos, a mensagem exceder o limite ou a fila estiver cheia, retorna `false`. Envios aceitos são enviados em ordem por `SendAsync`, um por vez. O callback confirma apenas a conclusão local do envio WebSocket; não é confirmação de aplicação.

Mensagens recebidas são classificadas no callback de rede e encaminhadas a filas separadas: `Max Messages Per Frame` limita mensagens de sessão/poses e `Max App Messages Per Frame` limita mensagens `app`. A fila recebida de aplicação aceita até 128 mensagens e 256 KiB; mensagens individuais continuam limitadas a 64 KiB UTF-8. Se esse limite for excedido, o cliente registra erro e fecha a conexão para evitar descartar silenciosamente pontos de desenho e deixar os participantes com estado divergente. A fila de saída e a de entrada são independentes.

### Eventos

| Evento | Tipo | Quando ocorre |
|---|---|---|
| `WelcomeReceived` | `Action<WelcomeMessage>` | Após o servidor aceitar o join e informar o jogador local e a lista atual da sala. |
| `ParticipantJoined` | `Action<PlayerJoinedMessage>` | Quando outro participante entra. Não dispara para o próprio jogador. |
| `ParticipantLeft` | `Action<PlayerLeftMessage>` | Quando um participante sai. |
| `PoseReceived` | `Action<PlayerPoseMessage>` | Para cada pose válida e mais recente recebida de outro jogador. |
| `AppMessageReceived` | `Action<AppMessage>` | Para cada mensagem `app` entregue, incluindo `playerId`, canal e payload. |
| `ErrorReceived` | `Action<ErrorMessage>` | Quando chega uma mensagem `error`; em rejeição antes do join o cliente fecha a conexão. |
| `ConnectionFailed` | `UnityEvent` | Quando o WebSocket reporta erro. |
| `Disconnected` | `UnityEvent` | Quando o WebSocket fecha. |

Exemplo de inscrição e remoção de listeners:

```csharp
using com.imd.ackit.multi_xr.CustomWebSocket;
using com.imd.ackit.multi_xr.Protocol;
using UnityEngine;

public sealed class SessionUi : MonoBehaviour
{
    [SerializeField] private WebSocketSessionClient client;

    private void OnEnable()
    {
        client.WelcomeReceived += OnWelcome;
        client.ErrorReceived += OnError;
    }

    private void OnDisable()
    {
        client.WelcomeReceived -= OnWelcome;
        client.ErrorReceived -= OnError;
    }

    private void OnWelcome(WelcomeMessage message) => Debug.Log($"Conectado como {message.selfId}");
    private void OnError(ErrorMessage message) => Debug.LogError($"{message.code}: {message.message}");
}
```

As mensagens de rede são enfileiradas e os eventos acima são disparados durante o processamento do componente na thread principal da Unity. O consumidor deve remover os listeners quando deixar de usar o componente.

## Tipos de mensagem do protocolo

Namespace: `com.imd.ackit.multi_xr.Protocol`.

Os tipos públicos correspondem às mensagens descritas em [protocol-v1.md](protocol-v1.md): `JoinMessage`, `WelcomeMessage`, `PlayerData`, `PlayerJoinedMessage`, `PlayerLeftMessage`, `PoseMessage`, `PlayerPoseMessage`, `AppMessage` e `ErrorMessage`. Os dados de pose usam posição `(x,y,z)` e rotação quaternion `(x,y,z,w)` relativos ao `NetworkOrigin`. O roster e `player_joined` carregam `displayColor` como `RRGGBB`.

Exemplo de conversão da cor para Unity:

```csharp
if (ColorUtility.TryParseHtmlString("#" + player.displayColor, out Color color))
{
    // Aplicar color ao material/visual desejado pelo projeto consumidor.
}
```

## RemotePlayerSpawner

Namespace: `com.imd.ackit.multi_xr.XR`.

O componente coordena os eventos de sessão e os avatares remotos. Campos do Inspector:

- `Session Client`: referência a `WebSocketSessionClient`; se omitida, procura o componente no mesmo GameObject.
- `Avatar Prefab`: prefab com `RemotePlayerAvatar` configurado.
- `Avatar Parent`: pai opcional das instâncias; se omitido, usa o próprio GameObject do spawner.

No `welcome`, cria avatares para os `quest_player` existentes; cria novos ao receber `ParticipantJoined`, aplica `PoseReceived` e remove no `ParticipantLeft`. Também limpa os avatares em erro de conexão ou desconexão. `desktop_viewer` não gera avatar no protocolo atual.

## RemotePlayerAvatar

Namespace: `com.imd.ackit.multi_xr.XR`.

No prefab, configure `Root` e `Head` (obrigatórios), e `Left Hand`/`Right Hand` se disponíveis. `Smoothing` (padrão `15`) controla a aproximação às poses recebidas; `Teleport Distance` (padrão `2` metros) define quando um salto provoca snap imediato. A instância fica inativa até a primeira pose válida.

Propriedades públicas somente para leitura:

```csharp
string PlayerId { get; }
string DisplayName { get; }
string DisplayColor { get; } // RRGGBB, sem #
```

O pacote disponibiliza a cor, mas não altera automaticamente materiais do prefab. O consumidor pode aplicar `DisplayColor` ao visual que escolher.

## Roteiro mínimo de configuração

1. Crie um GameObject de sessão e adicione `WebSocketSessionClient`.
2. Configure servidor, sala, papel, nome e cor no Inspector; associe o `NetworkOrigin` estável e os transforms locais necessários.
3. Se precisar de avatares remotos, crie o prefab com `RemotePlayerAvatar` e seus transforms, então adicione `RemotePlayerSpawner` e associe o prefab.
4. Inscreva os sistemas consumidores nos eventos necessários e remova as inscrições em `OnDisable`/`OnDestroy`.
5. Consulte [protocol-v1.md](protocol-v1.md) para o contrato de fio e [Contrato_Multiplayer_WebSocket_v1_Rascunho.md](../../../Contrato_Multiplayer_WebSocket_v1_Rascunho.md) para o contrato funcional cliente-servidor.

## Integração do Pointer no Cave-visualization

O componente `MultiXRPointerTransport` adapta o canal `pointer` de `WebSocketSessionClient` para `PointerTransport`. O prefab `Pointer Network (multi_xr)` contém `PointerNetworkSync` e `MultiXRPointerTransport` no mesmo GameObject; `WebSocketSessionClient` pode ser outro GameObject da cena e é localizado automaticamente. `DrawingBoard` e `MeasurementBoard` também são localizados/criados quando as referências do Inspector ficam vazias. Garanta que exista apenas uma sessão WebSocket ativa por participante.

O Pointer envia alterações e o feixe em mensagens `app`; ao entrar, pede snapshot a um peer. Snapshots são particionados em blocos de até 128 pontos ou 16 medidas, com limite interno de 28 KiB UTF-8 por payload. Alterações ao vivo ficam em buffer durante o snapshot (até 128 mensagens/256 KiB). Se a capacidade se esgotar, o transporte fecha a sessão, pois perder pontos poderia deixar o estado inconsistente. A limpeza compartilhada remove individualmente os objetos do autor local; limpeza global é proibida pelo servidor.
