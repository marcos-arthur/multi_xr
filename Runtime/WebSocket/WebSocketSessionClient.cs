using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using com.imd.ackit.multi_xr.Protocol;
using UnityEngine;
using UnityEngine.Events;
using WebSocketSharp;

namespace com.imd.ackit.multi_xr.CustomWebSocket
{
    public sealed class WebSocketSessionClient : MonoBehaviour
    {
        private const int MaxAppEnvelopeBytes = 64 * 1024;
        private const int MaxPendingAppBytes = 128 * 1024;
        private const int MaxPendingAppMessages = 256;
        private const int MaxAppRecipientLength = 128;
        private const int MaxQueuedAppReceiveBytes = 256 * 1024;
        private const int MaxQueuedAppReceiveMessages = 128;

        private sealed class PendingAppSend
        {
            public string Json;
            public int ByteCount;
        }

        private sealed class IncomingAppMessage
        {
            public string Json;
            public int ByteCount;
        }

        // ====== CONFIG GERAIS DO SERVIDOR ======
        [Header("Configurações de comunicação com o servidor")]
        [SerializeField] private string serverIp = "192.168.0.10";
        [SerializeField] private string serverPort = "7777";
        [SerializeField] private int maxMessagesPerFrame = 20; // evita travar em bursts
        [SerializeField, Min(1)] private int maxAppMessagesPerFrame = 10;
        [SerializeField] private string roomId = "sala-local";

        // ====== CONFIG GERAIS DO CLIENT ======
        [Header("Configurações do client")]
        //Pose ser quest_player ou desktop_viewer
        [SerializeField] private string role = "quest_player";
        [SerializeField] private string displayName = "Quest A";
        [SerializeField] private Color displayColor = new();


        // ====== CONFIG DE REDE (TAXAS) ======
        [Header("Taxas de envio (Hz)")]
        [SerializeField] private float playerSendHz = 20f;     // 20Hz é comum (50ms)
        
        // ====== CONFIG DOS TRANSFORMS DO PLAYER LOCAL ======
        [Header("Transforms locais")]
        [SerializeField] private Transform networkOrigin;
        [Tooltip("Set as XR Origin (XR Rig) Gameobject for now.")]
        [SerializeField] private Transform root;
        [SerializeField] private Transform head;
        [SerializeField] private Transform leftHand;
        [SerializeField] private Transform rightHand;

        [SerializeField] private float positionThreshold = 0.002f;
        [SerializeField] private float rotationThresholdDegrees = 0.5f;
        
        private WebSocket ws;
        private string playerId;
        private int poseSequence;
        private PoseMessage lastSentPose;
        private bool isClosing = false;
        private bool hasJoined = false;
        private readonly Dictionary<string, int> lastSequenceByPlayer = new();

        // Mesmo parado, cada jogador manda um pose de tempos em tempos
        private const float intervalbetweenPlayerUpdate = 1f;
        private float timeSinceLastPlayerUpdate;
        
        private Coroutine sendLoopPlayer;
        private readonly ConcurrentQueue<string> messageQueue = new ConcurrentQueue<string>();
        private readonly Queue<PendingAppSend> appSendQueue = new();
        private int queuedAppBytes;
        private int inFlightAppBytes;
        private bool appSendInProgress;
        private int appSendGeneration;
        private readonly object appReceiveLock = new();
        private readonly Queue<IncomingAppMessage> appReceiveQueue = new();
        private int queuedAppReceiveBytes;
        private int appReceiveOverflowSignaled;
        // Callbacks de rede podem rodar fora da thread Unity. Só classificamos JSON simples e usamos
        // filas thread-safe neles; eventos e APIs de cena são executados em Update.
        private readonly ConcurrentQueue<Action> mainThreadActions = new();

        public event Action<WelcomeMessage> WelcomeReceived;
        public event Action<PlayerJoinedMessage> ParticipantJoined;
        public event Action<PlayerLeftMessage> ParticipantLeft;
        public event Action<PlayerPoseMessage> PoseReceived;
        public event Action<AppMessage> AppMessageReceived;
        public event Action<ErrorMessage> ErrorReceived;
        public UnityEvent ConnectionFailed = new();
        public UnityEvent Disconnected = new();

        /// <summary>The stable shared coordinate frame used for outgoing and incoming poses.</summary>
        public Transform NetworkOrigin => networkOrigin;
        public string ServerIp => serverIp;
        public string ServerPort => serverPort;
        public string RoomId => roomId;
        public string Role => role;
        public string DisplayName => displayName;
        public Color DisplayColor => displayColor;
        void Start()
        {
            WelcomeReceived += message => Debug.Log($"Entrou: {message.selfId}; participantes existentes: {message.players?.Length ?? 0}");
            ParticipantJoined += message => Debug.Log($"Participante entrou: {message.displayName} ({message.playerId})");
            ParticipantLeft += message => Debug.Log($"Participante saiu: {message.playerId}; motivo: {message.reason}");
            ErrorReceived += message => Debug.LogError($"Erro do servidor: {message.code} - {message.message}");
            
            OpenConection();
        }

        void Update()
        {
            while (mainThreadActions.TryDequeue(out Action acao))
                acao();


            // Drena fila com limite por frame
            int processed = 0;
            while (processed < maxMessagesPerFrame && messageQueue.TryDequeue(out string json))
            {
                HandleIncomingMessage(json);
                processed++;
            }

            int processedApp = 0;
            while (Volatile.Read(ref appReceiveOverflowSignaled) == 0 &&
                   processedApp < Mathf.Max(1, maxAppMessagesPerFrame) &&
                   TryDequeueAppMessage(out string appJson))
            {
                HandleIncomingMessage(appJson);
                processedApp++;
            }
            
            if (!IsConnected()) return;
        }

        // Antes usava ws.IsAlive, que envia um ping e BLOQUEIA a thread principal esperando
        // o pong (era chamado todo frame). ReadyState so le o estado da conexao.
        public bool IsConnected()
        {
            return ws != null && ws.ReadyState == WebSocketState.Open;
        }

        void OpenConection()
        {
            JoinMessage joinMessage = new()
            {
                roomId = roomId,
                role = role,
                displayName = displayName,
                displayColor = ColorUtility.ToHtmlStringRGB(displayColor)
            };

            WebSocket socket = new($"ws://{serverIp}:{serverPort}");
            ws = socket;
            Interlocked.Exchange(ref appReceiveOverflowSignaled, 0);

            ws.OnOpen += (_, __) => mainThreadActions.Enqueue(() =>
            {
                if (socket.ReadyState != WebSocketState.Open) return;

                string json = JsonUtility.ToJson(joinMessage);
                socket.Send(json);

                lastSequenceByPlayer.Clear();
                // StartCoroutine(OnConnect());
                Debug.Log("✅ Conectado ao servidor. Join enviado.");
            });

            ws.OnMessage += (_, e) =>
            {
                // FromJson(string) aceita execução em background; o callback só classifica/enfileira.
                // Eventos e APIs de cena continuam sendo executados por Update na thread Unity.
                EnqueueIncomingMessage(e.Data);
            };

            ws.OnClose += (_, __) =>
            {
                Interlocked.Exchange(ref appReceiveOverflowSignaled, 1);
                mainThreadActions.Enqueue(() =>
                {
                    ResetAppSendQueue();
                    ResetAppReceiveQueue();
                    Disconnected?.Invoke();
                    isClosing = false;
                    Debug.Log("❌ Desconectado do servidor");
                    StopSendLoops();
                });
            };

            ws.OnError += (_, e) =>
            {
                string mensagem = e.Message;
                mainThreadActions.Enqueue(() =>
                {
                    ConnectionFailed?.Invoke();
                    Debug.LogError(mensagem);
                });
            };

            ws.ConnectAsync();
        }

        // =========================
        // RECEBIMENTO
        // =========================

        private void EnqueueIncomingMessage(string json)
        {
            MessageTypeWrapper wrapper;
            try
            {
                wrapper = JsonUtility.FromJson<MessageTypeWrapper>(json);
            }
            catch (ArgumentException)
            {
                // Mensagem malformada: não deixar uma exceção escapar do callback de rede.
                return;
            }

            if (wrapper == null || wrapper.type != "app")
            {
                messageQueue.Enqueue(json);
                return;
            }

            if (Volatile.Read(ref appReceiveOverflowSignaled) != 0)
                return;

            int byteCount = Encoding.UTF8.GetByteCount(json);
            string overflowReason = byteCount > MaxAppEnvelopeBytes
                ? "Uma mensagem app excedeu o limite de 64 KiB UTF-8."
                : null;
            lock (appReceiveLock)
            {
                if (Volatile.Read(ref appReceiveOverflowSignaled) == 0 && overflowReason == null &&
                    appReceiveQueue.Count < MaxQueuedAppReceiveMessages &&
                    queuedAppReceiveBytes + byteCount <= MaxQueuedAppReceiveBytes)
                {
                    appReceiveQueue.Enqueue(new IncomingAppMessage { Json = json, ByteCount = byteCount });
                    queuedAppReceiveBytes += byteCount;
                    return;
                }
            }

            SignalAppReceiveOverflow(overflowReason ?? "A fila de mensagens app excedeu sua capacidade.");
        }

        private bool TryDequeueAppMessage(out string json)
        {
            lock (appReceiveLock)
            {
                if (appReceiveQueue.Count == 0)
                {
                    json = null;
                    return false;
                }

                IncomingAppMessage message = appReceiveQueue.Dequeue();
                queuedAppReceiveBytes -= message.ByteCount;
                json = message.Json;
                return true;
            }
        }

        private void SignalAppReceiveOverflow(string reason)
        {
            if (Interlocked.Exchange(ref appReceiveOverflowSignaled, 1) != 0)
                return;

            mainThreadActions.Enqueue(() =>
            {
                Debug.LogError(reason + " Desconectando para evitar perda silenciosa do estado compartilhado.", this);
                CloseConnection();
            });
        }

        private void ResetAppReceiveQueue()
        {
            lock (appReceiveLock)
            {
                appReceiveQueue.Clear();
                queuedAppReceiveBytes = 0;
            }
        }

        void HandleIncomingMessage(string json)
        {
            var wrapper = JsonUtility.FromJson<MessageTypeWrapper>(json);
            if (wrapper == null || string.IsNullOrEmpty(wrapper.type)) return;

            switch (wrapper.type)
            {
                case "welcome":
                    WelcomeMessage welcome = JsonUtility.FromJson<WelcomeMessage>(json); 
                    playerId = welcome.selfId;
                    hasJoined = true;
                    WelcomeReceived?.Invoke(welcome);

                    if(welcome.role != "desktop_viewer") StartSendLoops();

                    Debug.Log($"Conexão estabelecida com {welcome.sessionId}");
                    break;

                case "player_joined":
                    PlayerJoinedMessage joined = JsonUtility.FromJson<PlayerJoinedMessage>(json); 
                    if(joined.playerId != playerId){
                        ParticipantJoined?.Invoke(joined);
                        Debug.Log($"Usuário de id {joined.playerId} entrou na sala");
                    }
                    break;

                case "player_left":
                    PlayerLeftMessage left = JsonUtility.FromJson<PlayerLeftMessage>(json); 
                    ParticipantLeft?.Invoke(left);
                    lastSequenceByPlayer.Remove(left.playerId);
                    Debug.Log($"Usuário de id {left.playerId} saiu da sala");
                    break;

                case "player_pose":
                    PlayerPoseMessage pose = JsonUtility.FromJson<PlayerPoseMessage>(json);
                    if(pose == null || string.IsNullOrEmpty(pose.playerId)) break;

                    if (lastSequenceByPlayer.TryGetValue(pose.playerId, out int lastSequence) && pose.sequence <= lastSequence) break;

                    lastSequenceByPlayer[pose.playerId] = pose.sequence;
                    PoseReceived?.Invoke(pose);
                    break;

                case "app":
                    AppMessage appMessage = JsonUtility.FromJson<AppMessage>(json);

                    if (Encoding.UTF8.GetByteCount(json) > MaxAppEnvelopeBytes ||
                        appMessage == null ||
                        appMessage.protocolVersion != 1 ||
                        appMessage.type != "app" ||
                        string.IsNullOrWhiteSpace(appMessage.playerId) ||
                        string.IsNullOrWhiteSpace(appMessage.channel) ||
                        !IsAllowedAppChannel(appMessage.channel) ||
                        string.IsNullOrWhiteSpace(appMessage.payload))
                        break;

                    AppMessageReceived?.Invoke(appMessage);
                    break;
                case "error":
                    ErrorMessage error = JsonUtility.FromJson<ErrorMessage>(json);
                    ErrorReceived?.Invoke(error);

                    if (!hasJoined)
                    {
                        CloseConnection();
                    }
                    break;

            }
        }

        // =========================
        // LOOPS DE ENVIO
        // =========================
        void StartSendLoops()
        {
            StopSendLoops();

            sendLoopPlayer = StartCoroutine(PlayerSendLoop());
        }

        void StopSendLoops()
        {
            if (sendLoopPlayer != null) StopCoroutine(sendLoopPlayer);

            sendLoopPlayer = null;
        }

        IEnumerator PlayerSendLoop()
        {
            var wait = new WaitForSeconds(1f / Mathf.Max(1f, playerSendHz));
            while (IsConnected())
            {
                if (!TryCapturePose(out PoseMessage current, out string error))
                {
                    Debug.LogError($"Envio de poses interrompido: {error}");
                    sendLoopPlayer = null;
                    yield break;
                }

                // Envia se mudou acima do threshold, ou como sinal de vida a cada 1 s
                // (o ADM no PC fica parado e precisa continuar aparecendo como ADM para os outros).
                if (HasPlayerPoseChanged(current) || Time.unscaledTime - timeSinceLastPlayerUpdate >= intervalbetweenPlayerUpdate)
                {
                    current.sequence = ++poseSequence;
                    ws.Send(JsonUtility.ToJson(current));

                    lastSentPose = current;
                    timeSinceLastPlayerUpdate = Time.unscaledTime;
                }

                yield return wait;
            }
        }

        private bool HasPlayerPoseChanged(PoseMessage current)
        {
            if(lastSentPose == null) return true;

            return  PoseChanged(current.root, lastSentPose.root) || 
                    PoseChanged(current.head, lastSentPose.head) || 
                    PoseChanged(current.leftHand, lastSentPose.leftHand) || 
                    PoseChanged(current.rightHand, lastSentPose.rightHand);
        }

        private bool TryCapturePose(out PoseMessage pose, out string error)
        {
            pose = null;
            error = null;

            if (networkOrigin == null)
            {
                error = "Network Origin não foi configurado.";
                return false;
            }

            Vector3 originScale = networkOrigin.lossyScale;
            if (Mathf.Abs(originScale.x - 1f) > 0.001f ||
                Mathf.Abs(originScale.y - 1f) > 0.001f ||
                Mathf.Abs(originScale.z - 1f) > 0.001f)
            {
                error = "Network Origin e seus pais devem manter escala (1, 1, 1).";
                return false;
            }

            if (root == null || head == null)
            {
                error = "É necessário configurar os transforms Root e Head.";
                return false;
            }

            pose = CapturePose();

            if (pose.IsInvalid())
            {
                error = "A pose capturada não contém Root ou Head válidos.";
                pose = null;
                return false;
            }

            return true;
        }

        private PoseMessage CapturePose()
        {
            return new PoseMessage
            {
                root = BuildPose(root),
                head = BuildPose(head),
                leftHand = BuildPose(leftHand),
                rightHand = BuildPose(rightHand),
            };
        }

        private PoseData BuildPose(Transform target)
        {
            if (target == null) return null;

            Vector3 localPosition = networkOrigin.InverseTransformPoint(target.position);
            Quaternion localRotation = Quaternion.Inverse(networkOrigin.rotation) * target.rotation;

            return new PoseData
            {
                position = new PositionData
                {
                    x = localPosition.x,
                    y = localPosition.y,
                    z = localPosition.z,
                },
                
                rotation = new RotationData
                {
                    x = localRotation.x,
                    y = localRotation.y,
                    z = localRotation.z,
                    w = localRotation.w
                }
            };
        }

        bool PositionChanged(PositionData a, PositionData b)
        {
            Vector3 delta = new(a.x - b.x, a.y - b.y, a.z - b.z);
            return delta.sqrMagnitude > positionThreshold * positionThreshold;
        }

        bool RotationChanged(RotationData a, RotationData b)
        {
            Quaternion qa = new(a.x,a.y,a.z,a.w);
            Quaternion qb = new(b.x,b.y,b.z,b.w);
            
            return Quaternion.Angle(qa, qb) > rotationThresholdDegrees;
        }

        private bool PoseChanged(PoseData current, PoseData previous)
        {
            if (current == null || previous == null) return current != previous;

            return PositionChanged(current.position, previous.position) || RotationChanged(current.rotation, previous.rotation);
        }

        
        /// <summary>
        /// Enqueues an application message for ordered asynchronous sending. True means accepted into
        /// the local queue, not acknowledged by the server.
        /// </summary>
        public bool SendAppMessage(string channel, string payload, string to = null)
        {
            if (!IsConnected() || !hasJoined ||
                string.IsNullOrWhiteSpace(channel) || !IsAllowedAppChannel(channel) ||
                string.IsNullOrWhiteSpace(payload))
                return false;

            if (to == string.Empty)
                to = null;
            else if (to != null &&
                     (string.IsNullOrWhiteSpace(to) || to.Length > MaxAppRecipientLength || to != to.Trim()))
                return false;

            var message = new AppMessage
            {
                protocolVersion = 1,
                type = "app",
                channel = channel,
                payload = payload,
                to = string.IsNullOrEmpty(to) ? null : to
            };

            string json = JsonUtility.ToJson(message);
            int byteCount = Encoding.UTF8.GetByteCount(json);
            if (byteCount > MaxAppEnvelopeBytes ||
                appSendQueue.Count + (appSendInProgress ? 1 : 0) >= MaxPendingAppMessages ||
                queuedAppBytes + inFlightAppBytes + byteCount > MaxPendingAppBytes)
                return false;

            appSendQueue.Enqueue(new PendingAppSend { Json = json, ByteCount = byteCount });
            queuedAppBytes += byteCount;
            StartNextAppSend();
            return true;
        }

        private static bool IsAllowedAppChannel(string channel) => channel == "pointer";

        private void StartNextAppSend()
        {
            if (appSendInProgress || appSendQueue.Count == 0)
                return;

            WebSocket socket = ws;
            if (socket == null || socket.ReadyState != WebSocketState.Open || !hasJoined)
            {
                ResetAppSendQueue();
                return;
            }

            PendingAppSend pending = appSendQueue.Dequeue();
            queuedAppBytes -= pending.ByteCount;
            inFlightAppBytes = pending.ByteCount;
            appSendInProgress = true;
            int generation = appSendGeneration;

            try
            {
                socket.SendAsync(pending.Json, completed =>
                    mainThreadActions.Enqueue(() => CompleteAppSend(socket, generation, completed)));
            }
            catch (Exception exception)
            {
                if (generation == appSendGeneration && ReferenceEquals(ws, socket))
                {
                    Debug.LogWarning($"Falha ao enviar mensagem da aplicação: {exception.Message}", this);
                    ResetAppSendQueue();
                }
            }
        }

        private void CompleteAppSend(WebSocket socket, int generation, bool completed)
        {
            // Uma conclusão atrasada de uma conexão antiga não pode mexer na fila atual.
            if (generation != appSendGeneration || !ReferenceEquals(ws, socket))
                return;

            inFlightAppBytes = 0;
            appSendInProgress = false;

            if (!completed)
            {
                Debug.LogWarning("O WebSocket não concluiu o envio da mensagem da aplicação.", this);
                ResetAppSendQueue();
                return;
            }

            StartNextAppSend();
        }

        private void ResetAppSendQueue()
        {
            appSendGeneration++;
            appSendQueue.Clear();
            queuedAppBytes = 0;
            inFlightAppBytes = 0;
            appSendInProgress = false;
        }

        private void OnApplicationQuit() => CloseConnection();
        private void OnDestroy() => CloseConnection();

        public void CloseConnection()
        {
            if(isClosing) return;

            WebSocket socket = ws;
            ws = null;
            ResetAppSendQueue();
            Interlocked.Exchange(ref appReceiveOverflowSignaled, 1);
            ResetAppReceiveQueue();

            if (socket == null) return;
            isClosing = true;

            if(socket.ReadyState == WebSocketState.Open || socket.ReadyState == WebSocketState.Connecting)
            {
                socket.CloseAsync();
            }
        }
    }
}
