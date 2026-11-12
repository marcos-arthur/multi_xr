using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using com.imd.ackit.multi_xr.Protocol;
using UnityEngine;
using UnityEngine.Events;
using WebSocketSharp;

namespace com.imd.ackit.multi_xr.CustomWebSocket
{
    public sealed class WebSocketSessionClient : MonoBehaviour
    {
        // ====== CONFIG GERAIS DO SERVIDOR ======
        [Header("Configurações do servidor")]
        [SerializeField] private string serverIp = "192.168.0.10";
        [SerializeField] private string serverPort = "7777";
        [SerializeField] private int maxMessagesPerFrame = 20; // evita travar em bursts
        [SerializeField] private string roomId = "sala-local";

        // ====== CONFIG GERAIS DO CLIENT ======
        //Pose ser quest_player ou desktop_viewer
        [SerializeField] private string role = "quest_player";
        [SerializeField] private string displayName = "Quest A";


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
        
        private readonly ConcurrentQueue<string> messageQueue = new ConcurrentQueue<string>();
        // Eventos do websocket (OnOpen/OnClose/OnError) chegam em outra thread; a API da Unity
        // so pode ser usada na thread principal, entao eles sao executados no Update.
        private readonly ConcurrentQueue<Action> mainThreadActions = new();

        public event Action<WelcomeMessage> WelcomeReceived;
        public event Action<PlayerJoinedMessage> ParticipantJoined;
        public event Action<PlayerLeftMessage> ParticipantLeft;
        public event Action<PlayerPoseMessage> PoseReceived;
        public event Action<ErrorMessage> ErrorReceived;
        public UnityEvent ConnectionFailed = new();
        public UnityEvent Disconnected = new();

        /// <summary>The stable shared coordinate frame used for outgoing and incoming poses.</summary>
        public Transform NetworkOrigin => networkOrigin;
        

        private Coroutine sendLoopPlayer;

        void Start()
        {
            playerId = "player_" + UnityEngine.Random.Range(1000, 9999);

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
                clientId = playerId,
                role = role,
                displayName = displayName
            };

            WebSocket socket = new($"ws://{serverIp}:{serverPort}");
            ws = socket;

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
                // OnMessage pode vir de thread diferente -> fila thread-safe
                messageQueue.Enqueue(e.Data);
            };

            ws.OnClose += (_, __) => mainThreadActions.Enqueue(() =>
            {
                Disconnected?.Invoke();
                isClosing = false;
                Debug.Log("❌ Desconectado do servidor");
                StopSendLoops();
            });

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

        void HandleIncomingMessage(string json)
        {
            var wrapper = JsonUtility.FromJson<MessageTypeWrapper>(json);
            if (wrapper == null || string.IsNullOrEmpty(wrapper.type)) return;

            switch (wrapper.type)
            {
                case "welcome":
                    WelcomeMessage welcome = JsonUtility.FromJson<WelcomeMessage>(json); 
                    WelcomeReceived?.Invoke(welcome);
                    playerId = welcome.selfId;

                    if(welcome.role != "desktop_viewer") StartSendLoops();
                    hasJoined = true;

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

        private void OnApplicationQuit() => CloseConnection();
        private void OnDestroy() => CloseConnection();

        public void CloseConnection()
        {
            if(isClosing) return;

            WebSocket socket = ws;
            ws = null;

            if (socket == null) return;
            isClosing = true;

            if(socket.ReadyState == WebSocketState.Open || socket.ReadyState == WebSocketState.Connecting)
            {
                socket.CloseAsync();
            }
        }
    }
}
