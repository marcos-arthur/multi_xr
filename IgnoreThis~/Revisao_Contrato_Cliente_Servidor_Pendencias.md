# Revisão do contrato e dos fluxos multiplayer

Revisão somente leitura de `Packages/multi_xr`, `Assets/Plugins/CloseSheepNetwork/Script` e do servidor `multi_xr_server`. Nenhum código foi alterado e nenhum teste foi executado.

## Resumo

O formato principal de `join`, `welcome`, `pose` e `player_pose` combina entre o contrato, o cliente Unity e o servidor. A revisão do servidor tentou separar as validações em funções, o que melhora a leitura, mas o estado atual ainda tem dois defeitos importantes em `validateTransform`: mãos ausentes/nulas são recusadas mesmo sendo opcionais, e as expressões de validação de `position` e `rotation` não detectam valores ausentes. Os vetores também ainda não são validados quanto a componentes numéricos finitos.

O servidor agora envia `player_joined` somente aos clientes que já estavam conectados antes do novo cliente, de acordo com o contrato. As dúvidas e recomendações detalhadas estão abaixo.

## 1. Entrega confiável das mensagens `error`

### Limite importante

Não é possível garantir que um cliente recebeu e processou um erro se a rede cair, o processo encerrar ou o socket fechar antes da entrega. O retorno positivo de `ws.send()` confirma envio/enfileiramento local no Bun, não que o código do Unity já tratou a mensagem. A documentação do Bun define `send()` como `-1` para mensagem enfileirada sob backpressure, `0` para mensagem descartada e `1+` para bytes enviados. O evento `drain` indica que o buffer foi escrito ao socket, não que o aplicativo remoto confirmou processamento.

Referência: https://bun.sh/docs/runtime/http/websockets

### Opção recomendada para este MVP

1. Envie o JSON `error` pelo WebSocket.
2. Não chame `ws.close()` imediatamente em seguida.
3. No cliente Unity, ao receber `error`, dispare o evento para a interface/log e feche a conexão pelo próprio cliente depois de tratar o erro.
4. No servidor, mantenha um timeout curto para fechar a conexão rejeitada caso o cliente não feche. Isso evita deixar sockets pendentes para sempre.
5. Verifique o retorno de `ws.send()`. Se for `0`, a mensagem foi descartada e não há entrega a aguardar. Se for `-1`, aguarde o callback `drain` antes do fechamento de fallback; não trate `drain` como confirmação de recebimento pelo Unity.

Essa abordagem assegura que o servidor não inicia o fechamento antes de o cliente ter a oportunidade de receber e agir sobre o erro, mas não elimina falhas abruptas da rede.

### Quando for necessária confirmação de aplicação

Acrescente um tipo de mensagem, por exemplo `error_ack`, que o cliente envia somente depois de processar `error`. O servidor guarda o estado de rejeição, aceita apenas esse ACK para a conexão ainda não ingressada e fecha após recebê-lo. Mantenha também um timeout de fallback. O ACK confirma que o aplicativo cliente tratou o frame, dentro dos limites de uma conexão ainda ativa.

No fluxo atual, a primeira mensagem de toda conexão ainda não ingressada deve ser `join`; portanto, implementar ACK exige um estado explícito para uma conexão rejeitada, para que `error_ack` não passe pela validação normal de primeiro `join`.

### Estado atual observado

As funções de validação ainda chamam `sendError(...)` e depois `ws.close()` imediatamente. `sendError` ignora o retorno de `ws.send()`. Isso mantém a incerteza de entrega que motivou a pergunta.

## 2. Revisão da validação nova no servidor

Arquivo: `multi_xr_server/index.ts`.

### Corrigir antes de usar com poses reais

**Mãos nulas são permitidas pelo contrato, mas agora invalidam a mensagem silenciosamente.** `validatePose` chama `validateTransform(..., false)` para cada mão. Se a mão for nula, `validateTransform` retorna `false`; `validatePose` então retorna `false` sem enviar `error`. O Quest segue conectado, mas a pose inteira não é retransmitida. Quando `transform` for nulo, a função deve retornar `true` se o campo for opcional e `false` com `INVALID_POSE` se for obrigatório.

**As verificações de `position` e `rotation` estão logicamente incorretas.** O código `!transform.position && transform.position` nunca produz uma condição verdadeira útil: se o campo existe, o primeiro termo é falso; se está ausente, o resultado continua nulo/indefinido e é tratado como falso. A mesma falha ocorre para `rotation`. Verifique diretamente `transform.position == null` e `transform.rotation == null`.

**Componentes numéricos ainda não são validados.** Para cada posição, aceite somente `x`, `y`, `z` numéricos e finitos. Para cada rotação, valide `x`, `y`, `z`, `w` da mesma forma; considere rejeitar quaternion de comprimento praticamente zero e normalizar ou exigir quaternion normalizado. A mensagem gerada pelo Unity usa números JSON, então não há motivo para aceitar strings numéricas.

**`displayName.trim()` pode lançar exceção para tipo incorreto.** `validateUserInfo` recebe um campo tipado como `string`, mas o JSON é dado não confiável. Verifique `typeof displayName === "string"` antes de chamar `.trim()`, e mantenha o limite de 1–32 caracteres após remover espaços externos. Sem essa verificação, o `catch` geral transforma o problema em `UNKNOWN_MESSAGE_ERROR` em vez de um erro de campo previsível.

### Outros detalhes de protocolo

- `validateSequence` funciona com o valor inicial `-1`, mas a condição baseada em truthiness (`ws.data.sequence && ...`) é menos clara do que comparar explicitamente com `undefined`. Atualize a sequência somente após toda a pose ter passado pela validação, como ocorre hoje em `handlePose`.
- Após `join`, tipos de mensagem desconhecidos ou um segundo `join` são ignorados sem erro. Defina o comportamento e documente/implemente `INVALID_MESSAGE` se quiser que o servidor informe essas violações.
- `validateRoomInfo` limita o servidor a `sala-local`, e `clients` é um único `Set`. É coerente com o protótipo de sala única, mas não implementa isolamento para múltiplas salas apesar do campo `roomId`.
- O servidor manda `player_joined` aos clientes já existentes antes de adicionar o novo socket ao `clients`; isso corresponde ao contrato e ao snapshot de participantes em `welcome`.
- Os códigos de erro efetivamente usados precisam ser enumerados no contrato. Além dos já listados, o servidor usa `ROOM_NOT_FOUND`, `INVALID_NAME` e `UNKNOWN_MESSAGE_ERROR`.

## 3. Poses antigas e sequência no cliente

Sim: no estado atual, o componente recebe `player_pose`, desserializa e invoca `PoseReceived` sem comparar `sequence`. Se uma pose de sequência menor chegar ao consumidor depois de uma maior, o consumidor poderá aplicar a antiga e fazer o avatar remoto recuar para uma posição anterior.

Porém, WebSocket roda sobre TCP, que entrega bytes ordenados dentro de uma conexão, e o servidor processa e retransmite cada pose nessa ordem. Assim, em condições normais de uma única conexão, é improvável que a ordem se inverta. O filtro de sequência é uma proteção defensiva para quando houver filas paralelas, processamento assíncrono, reconexões ou mudanças futuras de transporte.

A filtragem pode ficar no consumidor que atualiza cada avatar: mantenha `lastSequence[playerId]`; ignore quando `incoming.sequence <= lastSequence[playerId]`; caso contrário, grave a sequência e aplique a pose. Limpe a entrada ao receber `player_left`. Isso também evita misturar as sequências independentes de jogadores diferentes.

## 4. Frequência no BunWebSocketClient original

No `Assets/Plugins/CloseSheepNetwork/Script/BunWebSocketClient.cs`:

- `playerSendHz` é `20f`, portanto o loop amostra aproximadamente a cada `1 / 20` segundo (50 ms), usando `WaitForSeconds`.
- O loop não envia uma pose a cada amostra obrigatoriamente. Envia quando `HasPlayerPoseChanged()` supera os limites ou quando passou `intervaloSinalDeVida = 1f` desde o último envio.
- `posEps` é `0.002f` (2 mm); `rotEpsDeg` é `0.5f`.
- `HasPlayerPoseChanged()` compara `body`, cabeça e mãos; posição usa distância quadrática contra o epsilon e rotação compara `eulerAngles` como `Vector3`.
- `SendPlayerUpdate()` serializa os transforms atuais, chama `UpdatePlayerCache()` e grava `ultimoEnvioPlayer = Time.unscaledTime`.
- `AoConectar()` chama `PrimeCacheStates()` antes de `StartSendLoops()`. Portanto, a primeira mensagem normalmente só ocorre quando há mudança acima do limite ou após o primeiro heartbeat de 1 s.
- O cliente novo também tem loop de 20 Hz e heartbeat de 1 s, com os mesmos valores iniciais de limites. Como `lastSentPose` começa nulo, ele envia a pose inicial logo no primeiro ciclo. Em seguida, filtra mudanças comparando posição relativa à origem e quaternion com `Quaternion.Angle`.

Referências internas: `BunWebSocketClient.cs`, métodos `AoConectar`, `PlayerSendLoop`, `SendPlayerUpdate`, `PrimeCacheStates`, `HasPlayerPoseChanged`, `UpdatePlayerCache`, `Changed`.

Conclusão: o contrato sugere 15 mensagens/s, enquanto os dois clientes configuram uma amostragem máxima de 20 Hz. A taxa real não é fixa: movimento acima do limiar pode gerar até cerca de 20 mensagens/s; parado, há um heartbeat a cada segundo. Recomendo alinhar a documentação a essa política, em vez de chamar 15 Hz de frequência real.

## 5. Espaço de coordenadas das poses

O projeto original serializa posições em coordenadas de mundo (`Transform.position`) e rotações como ângulos Euler (`Transform.eulerAngles`). O cliente novo calcula posição usando `networkOrigin.InverseTransformPoint` e rotação relativa usando `Inverse(networkOrigin.rotation) * target.rotation`. A abordagem relativa é a adequada para o protocolo novo, desde que todos reconstruam a mesma referência lógica.

### Configuração recomendada

1. Crie um `NetworkOrigin` estável na cena, separado do `XR Origin` móvel do headset.
2. Faça-o representar a mesma origem e orientação lógicas em todos os clientes (mesmo ambiente virtual/mesmo ponto de referência). Cada cliente pode ter o objeto em coordenadas de mundo locais diferentes somente se a conversão de entrada e saída for consistente com esse referencial compartilhado.
3. Use escala `(1, 1, 1)` no `NetworkOrigin` e nos pais que afetem sua escala. O protocolo diz que posições estão em metros/Unity units; `InverseTransformPoint` incorpora escala, portanto escala diferente de 1 muda os valores enviados e quebra essa interpretação.
4. Ao enviar: posição relativa `networkOrigin.InverseTransformPoint(target.position)`; rotação relativa `Quaternion.Inverse(networkOrigin.rotation) * target.rotation`.
5. Ao receber: reconstruir posição com `networkOrigin.TransformPoint(remote.position)` e rotação com `networkOrigin.rotation * remote.rotation`; não atribuir diretamente a posição relativa a um Transform que espera coordenadas de mundo.
6. Se `NetworkOrigin` for identidade (posição zero, rotação identidade, escala um) em todos os projetos, as coordenadas relativas coincidem com coordenadas de mundo, que é uma migração simples do cliente legado.

Se quiser que coordenadas de rede signifiquem metros mesmo quando o objeto de referência tem escala não unitária, a alternativa é calcular sem aplicar escala: `Quaternion.Inverse(origin.rotation) * (target.position - origin.position)` e reconstruir com `origin.position + origin.rotation * relativePosition`. Para o MVP, é mais simples proibir escala diferente de 1 no `NetworkOrigin`.

## Ordem sugerida

1. Corrigir retorno para mãos opcionais e checks de campos ausentes em `validateTransform`.
2. Validar tipo e finitude dos componentes numéricos de posição/rotação e tipo de `displayName`.
3. Escolher a política de entrega de `error`: fechamento pelo cliente após receber + timeout no servidor; adicionar ACK de aplicação apenas se a confirmação for requisito.
4. Documentar os códigos de erro e a política de frequência; indicar sala única enquanto esse for o limite.
5. No consumidor de `PoseReceived`, filtrar sequência por `playerId` e reconstruir pose no espaço local usando `NetworkOrigin`.
6. Depois das alterações, testar handshake/error, mãos nulas e pose inválida, além de pose válida e descarte de sequência antiga.
