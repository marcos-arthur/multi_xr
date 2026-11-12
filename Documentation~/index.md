# Multi XR

Pacote de transporte WebSocket e presença XR genérico. O núcleo do pacote não depende dos papéis, cadeiras, instrumentos, experiências, cosméticos ou prefabs do FullDjembe.

## Presença e avatares remotos

`WebSocketSessionClient` expõe eventos de `welcome`, entrada/saída de participantes e poses. `RemotePlayerSpawner` consome esses eventos para:

- criar um avatar para cada `quest_player` já listado no `welcome`;
- criar um avatar para um `quest_player` que entrar depois;
- aplicar poses remotas e descartar sequências antigas;
- destruir o avatar quando chegar `player_left` ou a conexão cair.

`displayColor` acompanha nome e papel no `join`, roster e evento `player_joined`; o pacote o armazena em `RemotePlayerAvatar.DisplayColor` e deixa a aplicação visual ao prefab consumidor. Clientes `desktop_viewer` não recebem avatar. O protocolo v1 não envia dados de skin, cadeira ou cosméticos.

### Preparar o prefab

1. Crie/adapte um prefab de avatar sem dependência de `BunVRRemotePlayers` ou `RemotePlayerRefs`.
2. Adicione `RemotePlayerAvatar` ao prefab.
3. Associe `Root` e `Head` (pivôs distintos); associe mãos se o rig tiver esses pontos.
4. Ajuste `Smoothing` (padrão 15) e `Teleport Distance` (padrão 2 m), se necessário.

### Configurar a cena

1. Use `NetworkOrigin` estável, separado do tracking/locomoção do `XR Origin`, com escala efetiva unitária.
2. Adicione `WebSocketSessionClient` e configure servidor, sala, papel, nome e cor.
3. Adicione `RemotePlayerSpawner`, associe o client e o prefab. `Avatar Parent` é opcional.
4. O avatar fica inativo até receber a primeira pose válida; depois as poses são suavizadas, com snap em saltos maiores que `Teleport Distance`.

## Documentação

- [Contrato do protocolo WebSocket v1](protocol-v1.md)
- [API e guia de integração](api-integracao.md)
- [Pendências do WebSocketSessionClient](Revisao_WebSocketSessionClient_Pendencias.md)
- [Revisão do contrato e fluxos](Revisao_Contrato_Cliente_Servidor_Pendencias.md)
- [Implementação de presença, poses e coordenadas](Implementacao_Presenca_Poses_e_Coordenadas.md)
