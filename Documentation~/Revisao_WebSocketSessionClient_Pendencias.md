# Revisão do `WebSocketSessionClient` — melhorias pendentes

Revisado em 11/11/2026. Este documento registra ajustes de menor prioridade observados na versão atual de `Packages/com.imd.ackit.xr-presence/Runtime/WebSocket/WebSocketSessionClient.cs`. Não substitui a revisão dos contratos nem do servidor.

## Situação geral

Não encontrei, nesta leitura, um bloqueio crítico evidente no fluxo principal de pose: o cliente envia `join`, aguarda `welcome`, captura poses relativas a `networkOrigin`, exige `root` e `head`, ignora mãos não configuradas e interrompe o loop com erro se a pose obrigatória não puder ser capturada. A sequência também aumenta a cada envio.

## Melhorias para fazer depois

1. **Validar configuração antes de abrir o socket.** Validar `serverIp`, `serverPort`, `roomId`, `role` e `displayName` antes de `ConnectAsync()`. O servidor exige `quest_player` ou `desktop_viewer` e nome entre 1 e 32 caracteres. Se inválidos, mostrar erro local e não iniciar a conexão.

2. **Não conectar pelo `Start()` sem opção de configuração externa.** Para reutilizar o pacote em outros jogos, pode ser mais flexível expor um método público como `Connect(...)` ou separar a configuração do ciclo de vida do componente. No estado atual, `Start()` conecta automaticamente com os campos do Inspector.

3. **Unificar ou documentar eventos `OnError` e `OnClose`.** Uma falha ao conectar pode gerar ambos: `ConnectionFailed` em `OnError` e `Disconnected` em `OnClose`. Consumidores da API devem saber que podem receber os dois eventos para uma única tentativa. Alternativamente, deduplicar a notificação ou incluir um estado/motivo no evento.

4. **Lidar com JSON malformado ou mensagem inesperada.** `JsonUtility.FromJson` pode falhar ou produzir objeto incompleto. Envolver a desserialização em tratamento de exceção e validar o conteúdo antes de invocar eventos impede que uma mensagem ruim interrompa o processamento de outras mensagens.

5. **Tratar respostas de erro do servidor segundo a política desejada.** O cliente dispara `ErrorReceived`, mas não fecha nem altera estado após receber `error`. Definir se erros de protocolo/sala devem apenas ser exibidos ou também encerrar a sessão; alguns erros podem não ser fatais.

6. **Rever a fila de mensagens ao desconectar.** `Update()` continua drenando `messageQueue` mesmo se o socket já não estiver conectado, o que é útil para processar mensagens já recebidas. Decida se mensagens antigas devem ser processadas após desconexão; se não, limpe a fila no fechamento.

7. **Proteger `socket.Send()` contra exceções e encerramento concorrente.** A chamada é feita pela coroutine na thread principal, mas o socket pode fechar entre a verificação de conexão e o envio. Tratar falha de envio evita que a coroutine gere exceção não tratada.

8. **Evitar duplicidade de inicialização do loop.** Uma mensagem `welcome` repetida chamaria `StartSendLoops()` novamente. O método para o loop existente antes de iniciar outro, então não cria dois loops, mas pode ser útil rejeitar/registrar um `welcome` duplicado como mensagem inesperada.

9. **Limpar e renomear campos antigos.** `intervalbetweenPlayerUpdate` e `timeSinceLastPlayerUpdate` ainda usam nomenclatura herdada. Considere nomes como `poseHeartbeatIntervalSeconds` e `lastPoseSentAt`. O comentário do ciclo deve explicar que o envio periódico mantém o participante ativo, sem citar ADM/músico do jogo antigo.

10. **Rever configurações de taxa e limiares.** `playerSendHz` é limitado a pelo menos 1 Hz durante o loop. Também pode validar `positionThreshold` e `rotationThresholdDegrees` para não aceitar valores negativos no Inspector. Considere expor o intervalo periódico como configuração se outros projetos precisarem ajustá-lo.

11. **Considerar configuração de URL/segurança em vez de IP e porta separados.** Para LAN, `ws://IP:porta` atende ao teste atual. Quando houver servidor público, provavelmente será necessário suportar `wss://` e configurar a URL sem concatenar manualmente host e porta.

12. **Considerar cancelamento explícito ao desativar o GameObject.** Há fechamento em `OnApplicationQuit()` e `OnDestroy()`. Se o componente puder ser desativado e reativado sem destruir o objeto, definir o comportamento esperado em `OnDisable()` também.

## Observações sobre poses

- `TryCapturePose()` retorna `false` e `PlayerSendLoop()` usa `yield break`, então a pose inválida não é enviada. Para erros de referência obrigatória, isso é coerente; corrigir as referências em runtime exigirá reiniciar o loop.
- `PoseMessage.IsInvalid()` valida `root` e `head`, que são obrigatórios para o servidor. As mãos são opcionais e podem ser `null`.
- `lastSentPose` só é atualizado após o envio, então a primeira pose é enviada mesmo se o jogador estiver parado.
- O espaço relativo depende de todos os clientes usarem uma origem de rede compatível. Esse alinhamento é responsabilidade da cena/configuração do projeto consumidor.

## Ordem sugerida

Para testes LAN, as prioridades seguintes são: validar o `join` localmente; tratar falhas de `Send`; decidir a semântica de `ConnectionFailed` versus `Disconnected`; e definir se mensagens recebidas antes do fechamento devem ser processadas. As melhorias de URL segura e configuração pública podem ficar para quando o servidor externo for definido.
