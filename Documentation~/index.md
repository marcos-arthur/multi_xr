# Multi XR

Pacote de transporte WebSocket e presença XR genérico. O núcleo do pacote não depende dos papéis, cadeiras, instrumentos, experiências, cosméticos ou prefabs do FullDjembe.

## Presença e avatares remotos

`WebSocketSessionClient` expõe eventos de `welcome`, entrada/saída de participantes e poses. `RemotePlayerSpawner` consome esses eventos para:

- criar um avatar para cada `quest_player` já listado no `welcome`;
- criar um avatar para um `quest_player` que entrar depois;
- aplicar poses remotas e descartar sequências antigas (o filtro de sequência fica no cliente de sessão);
- destruir o avatar quando chegar `player_left` ou a conexão cair.

Clientes `desktop_viewer` não recebem avatar. O pacote de protocolo v1 não envia dados de skin, cadeira ou cosméticos; projetos consumidores escolhem um prefab genérico ou estendem seus próprios metadados separadamente.

### Preparar o prefab

1. Crie ou adapte um prefab de avatar no projeto consumidor. Ele pode ser construído a partir do modelo que já existe no jogo, mas não deve depender de `BunVRRemotePlayers` ou `RemotePlayerRefs`.
2. Adicione `RemotePlayerAvatar` ao prefab.
3. No Inspector do componente, associe os transforms de `Root` e `Head`; associe `Left Hand` e `Right Hand` se o rig tiver esses pontos. Mãos sem tracking podem ficar sem pose; uma pose de mão nula mantém a última pose conhecida.
4. Ajuste `Smoothing` (padrão 15) e `Teleport Distance` (padrão 2 m), se necessário.

### Configurar a cena

1. Na cena compartilhada, mantenha um `NetworkOrigin` estável, separado do tracking/locomoção do `XR Origin`, com `Lossy Scale` aproximadamente `(1, 1, 1)`.
2. No mesmo GameObject do `WebSocketSessionClient` ou em outro GameObject ativo, adicione `RemotePlayerSpawner`.
3. Associe o `WebSocketSessionClient` e o prefab com `RemotePlayerAvatar`. O cliente expõe sua referência `NetworkOrigin`, então não é preciso configurar uma segunda origem no spawner.
4. `Avatar Parent` é opcional; vazio, os avatares ficam filhos do objeto do spawner.
5. O avatar fica inativo até receber a primeira pose válida, evitando que apareça temporariamente na origem da cena. A primeira pose é aplicada imediatamente; as seguintes são suavizadas. Saltos maiores que `Teleport Distance` são aplicados sem interpolação.

O cliente transforma posição relativa em coordenadas de mundo com `origin.position + origin.rotation * relativePosition`; portanto, a escala do `NetworkOrigin` e dos pais precisa ser unitária. O componente cliente interrompe o envio de poses com uma mensagem de erro se essa condição não for atendida.

### Fluxo que substitui a parte genérica do `BunVRRemotePlayers`

- `welcome.players` substitui depender da primeira atualização periódica para descobrir quem já está na sala.
- `ParticipantJoined` cria jogadores que chegam depois.
- `PoseReceived` posiciona root/cabeça/mãos no referencial de rede e suaviza o movimento.
- `ParticipantLeft` remove o avatar.

A lógica antiga específica do FullDjembe — ADM/músico, cadeira, prefab de skin, cosméticos, tambor dummy e dados da experiência — fica fora do pacote.

## Documentação

- [Protocolo WebSocket v1](protocol-v1.md)
- [Pendências do WebSocketSessionClient](Revisao_WebSocketSessionClient_Pendencias.md)
- [Revisão do contrato e fluxos](Revisao_Contrato_Cliente_Servidor_Pendencias.md)
- [Implementação de presença, poses e coordenadas](Implementacao_Presenca_Poses_e_Coordenadas.md)
