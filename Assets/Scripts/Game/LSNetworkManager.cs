using UnityEngine;
using Mirror;
using System;
using System.Collections.Generic;
using UnityEngine.SceneManagement;

/// <summary>The character a client picked, sent before its player is added.</summary>
public struct CharacterChoiceMessage : NetworkMessage
{
    public string characterName;
}

public class LSNetworkManager : NetworkManager
{
    public static event Action OnDisconnectedEvent;

    [Tooltip("Every playable character. Players get the one they picked on the character screen, bots and players who never picked get one at random; a player keeps theirs from the lobby into the match.")]
    public GameObject[] characterPrefabs;

    private readonly Dictionary<int, GameObject> characterByConnection = new Dictionary<int, GameObject>();

    public override void Awake()
    {
        // Clients can only spawn prefabs they have registered.
        foreach (GameObject prefab in characterPrefabs)
        {
            if (prefab != null && prefab != playerPrefab && !spawnPrefabs.Contains(prefab))
                spawnPrefabs.Add(prefab);
        }

        base.Awake();
    }

    public GameObject RandomCharacter()
    {
        return characterPrefabs != null && characterPrefabs.Length > 0
            ? characterPrefabs[UnityEngine.Random.Range(0, characterPrefabs.Length)]
            : playerPrefab;
    }

    public override void OnStartServer()
    {
        base.OnStartServer();
        NetworkServer.RegisterHandler<CharacterChoiceMessage>(OnCharacterChoice);
    }

    private void OnCharacterChoice(NetworkConnectionToClient conn, CharacterChoiceMessage message)
    {
        GameObject prefab = Array.Find(characterPrefabs, p => p != null && p.name == message.characterName);

        if (prefab != null)
            characterByConnection[conn.connectionId] = prefab;
    }

    public override void OnClientConnect()
    {
        // Ahead of base, which asks for the player: the server reads messages in order,
        // so the choice is known by the time the player is spawned.
        PlayerProfileSO profile = ScriptableObject.CreateInstance<PlayerProfileSO>();
        profile.LoadProfile();
        NetworkClient.Send(new CharacterChoiceMessage { characterName = profile.characterName });
        Destroy(profile);

        base.OnClientConnect();
    }

    private GameObject CharacterFor(NetworkConnectionToClient conn)
    {
        if (!characterByConnection.TryGetValue(conn.connectionId, out GameObject prefab) || prefab == null)
            characterByConnection[conn.connectionId] = prefab = RandomCharacter();

        return prefab;
    }

    /// <summary>
    /// Gate for every incoming connection. Once the host starts the match the door is
    /// closed, and the lobby capacity is enforced here rather than only by discovery.
    /// </summary>
    public override void OnServerConnect(NetworkConnectionToClient conn)
    {
        // The host's own local connection is never rejected.
        if (conn == NetworkServer.localConnection)
        {
            base.OnServerConnect(conn);
            return;
        }

        LSMatchManager match = LSMatchManager.Instance;

        if (match != null)
        {
            if (match.matchStarted)
            {
                Debug.LogWarning(
                    $"[LSNetworkManager] Rejecting connection {conn.connectionId}: the match has already started."
                );

                conn.Disconnect();
                return;
            }

            if (numPlayers >= match.maxPlayers)
            {
                Debug.LogWarning(
                    $"[LSNetworkManager] Rejecting connection {conn.connectionId}: lobby is full " +
                    $"({numPlayers}/{match.maxPlayers})."
                );

                conn.Disconnect();
                return;
            }
        }

        base.OnServerConnect(conn);
    }

    public override void OnServerAddPlayer(NetworkConnectionToClient conn)
    {
        if (SceneManager.GetActiveScene().name == "LobbyScene")
        {
            Transform startPos = GetStartPosition();
            Vector3 spawnPos = startPos != null ? startPos.position : Vector3.zero;
            Quaternion spawnRot = startPos != null ? startPos.rotation : Quaternion.identity;

            // Dynamic Spacing based on current player count
            int playerIndex = numPlayers;
            float xOffset = 0f;

            if (playerIndex > 0)
            {
                int multiplier = (playerIndex + 1) / 2;
                float sign = (playerIndex % 2 != 0) ? 1f : -1f;
                xOffset = multiplier * 2.0f * sign;
            }

            // Apply spacing offset relative to the spawn rotation
            spawnPos += spawnRot * Vector3.right * xOffset;

            // Perfect Grounding: raycast down from an elevated position to find the ground
            Vector3 rayStart = spawnPos + Vector3.up * 10f;
            if (Physics.Raycast(rayStart, Vector3.down, out RaycastHit hit, 20f))
            {
                spawnPos.y = hit.point.y;
            }

            GameObject prefab = CharacterFor(conn);
            GameObject player = Instantiate(prefab, spawnPos, spawnRot);
            player.name = $"{prefab.name} [connId={conn.connectionId}]";
            NetworkServer.AddPlayerForConnection(conn, player);
        }
        else if (LSMatchManager.Instance != null &&
                 LSMatchManager.Instance.ServerTryGetPlayerSpawn(conn.connectionId, out Vector3 spawnPos, out Quaternion spawnRot))
        {
            // A random spot for the player's team, away from every other team.
            GameObject prefab = CharacterFor(conn);
            GameObject player = Instantiate(prefab, spawnPos, spawnRot);
            player.name = $"{prefab.name} [connId={conn.connectionId}]";
            NetworkServer.AddPlayerForConnection(conn, player);
        }
        else
        {
            base.OnServerAddPlayer(conn);
        }
    }


    /// <summary>
    /// Reports a real network disconnect to MatchTracker before Mirror removes the
    /// player's object. Scene changes do not call this, so lobby -> gameplay respawns
    /// are never mistaken for eliminations.
    /// </summary>
    public override void OnServerDisconnect(NetworkConnectionToClient conn)
    {
        // By connection ID, which MatchTracker keys participants by. The player object
        // is often already gone here - a client can drop while GameScene is loading,
        // before its new player is spawned - and a lookup through the object would
        // then miss the leaver, leaving them "alive" forever so the match never ended.
        if (conn != null &&
            LSMatchManager.Instance != null &&
            LSMatchManager.Instance.matchStarted &&
            MatchTracker.Instance != null)
        {
            MatchTracker.Instance.ServerNotifyConnectionLost(conn.connectionId);
        }

        if (conn != null)
            characterByConnection.Remove(conn.connectionId);

        base.OnServerDisconnect(conn);
    }

    public override void OnClientDisconnect()
    {
        base.OnClientDisconnect();
        OnDisconnectedEvent?.Invoke();

        // Only trigger for pure clients, not the host itself.
        // This also covers a client refused because the match had already started:
        // the sequence resets lobby UI, tears the client down and routes back to the main menu.
        if (!NetworkServer.active)
        {
            if (GameUIManager.Instance != null)
            {
                GameUIManager.Instance.TriggerDisconnectSequence();
            }
        }
    }

    public override void OnStopServer()
    {
        base.OnStopServer();
        characterByConnection.Clear();
        OnDisconnectedEvent?.Invoke();
    }
}