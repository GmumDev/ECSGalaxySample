using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.XR;

namespace Galaxy
{
    [BurstCompile]
    [UpdateInGroup(typeof(PresentationSystemGroup))]
    public partial struct GameCameraSystem : ISystem
    {
        private Unity.Mathematics.Random _random;
        // XR 디바이스 캐시 (클래스 필드)
        private InputDevice _leftHand;
        private InputDevice _rightHand;

        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            _random = Unity.Mathematics.Random.CreateFromIndex(0);
            state.RequireForUpdate<GameIsSimulating>();
            state.RequireForUpdate<SimulationRate>();
            state.RequireForUpdate(SystemAPI.QueryBuilder().WithAll<GameCamera>().Build());
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            if (!_leftHand.isValid || !_rightHand.isValid)
            {
                Debug.Log($"LeftHand valid: {_leftHand.isValid}");
                Debug.Log($"RightHand valid: {_rightHand.isValid}");
                _leftHand = InputDevices.GetDeviceAtXRNode(XRNode.LeftHand);
                _rightHand = InputDevices.GetDeviceAtXRNode(XRNode.RightHand);
                return; // 이번 프레임은 입력 스킵
            }

            _leftHand.TryGetFeatureValue(CommonUsages.primary2DAxis, out Vector2 leftStick);   // 이동 XZ
            _leftHand.TryGetFeatureValue(CommonUsages.triggerButton, out bool leftTrigger); // 상승 (E 대체)
            _leftHand.TryGetFeatureValue(CommonUsages.gripButton, out bool leftGrip);    // 하강 (Q 대체)
            _rightHand.TryGetFeatureValue(CommonUsages.primary2DAxis, out Vector2 rightStick);  // 시선 회전
            _rightHand.TryGetFeatureValue(CommonUsages.trigger, out float rightTrigger);// Zoom (아날로그)
            _rightHand.TryGetFeatureValue(CommonUsages.gripButton, out bool rightGrip);   // Sprint
            _rightHand.TryGetFeatureValue(CommonUsages.primaryButton, out bool aButton);
            // Collect input
            CameraInputs cameraInputs = new CameraInputs
            {
                Move = new float3(
                        leftStick.x,                                    // A/D → 왼손 Thumbstick X
                        (leftTrigger ? 1f : 0f) + (leftGrip ? -1f : 0f), // E/Q → 왼손 Trigger/Grip
                        leftStick.y),                                   // W/S → 왼손 Thumbstick Y
                Look = new float2(
                        rightStick.x,                                   // Mouse X → 오른손 Thumbstick X
                        rightStick.y),                                  // Mouse Y → 오른손 Thumbstick Y
                Zoom = rightTrigger,                                // ScrollWheel → 오른손 Trigger 아날로그
                Sprint = rightGrip,                                 // LeftShift → 오른손 Grip
                SwitchMode = aButton,                               // Z → 오른손 A버튼
            };
            cameraInputs.Move = math.normalizesafe(cameraInputs.Move) *
                                math.saturate(math.length(cameraInputs.Move)); // Clamp move inputs magnitude to 1

            // Camera target switching
            Entity nextTargetPlanet = Entity.Null;
            Entity nextTargetShip = Entity.Null;
            bool switchShip = Input.GetKeyDown(KeyCode.X);
            if (cameraInputs.SwitchMode || switchShip)
            {
                EntityQuery planetsQuery = SystemAPI.QueryBuilder().WithAll<Planet>().Build();
                NativeArray<Entity> planetEntities = planetsQuery.ToEntityArray(Allocator.Temp);
                if (planetEntities.Length > 0)
                {
                    nextTargetPlanet = planetEntities[_random.NextInt(planetEntities.Length)];
                }

                planetEntities.Dispose();

                EntityQuery shipsQuery = SystemAPI.QueryBuilder().WithAll<Ship>().Build();
                NativeArray<Entity> shipEntities = shipsQuery.ToEntityArray(Allocator.Temp);
                if (shipEntities.Length > 0)
                {
                    nextTargetShip = shipEntities[_random.NextInt(shipEntities.Length)];
                }

                shipEntities.Dispose();
            }

            GameCameraJob job = new GameCameraJob
            {
                DeltaTime = SystemAPI.Time.DeltaTime,
                CameraInputs = cameraInputs,
                NextTargetPlanet = nextTargetPlanet,
                NextTargetShip = nextTargetShip,
                LocalToWorldLookup = SystemAPI.GetComponentLookup<LocalToWorld>(false),
            };
            job.Schedule();
        }

        [BurstCompile]
        [WithAll(typeof(Simulate))]
        public partial struct GameCameraJob : IJobEntity
        {
            public float DeltaTime;
            public CameraInputs CameraInputs;
            public Entity NextTargetPlanet;
            public Entity NextTargetShip;

            public ComponentLookup<LocalToWorld> LocalToWorldLookup;

            void Execute(Entity entity, ref LocalTransform transform, ref GameCamera gameCamera)
            {
                if (gameCamera.IgnoreInput)
                    return;

                // Mode switch
                if (CameraInputs.SwitchMode)
                {
                    switch (gameCamera.CameraMode)
                    {
                        case GameCamera.Mode.Fly:
                            gameCamera.CameraMode = GameCamera.Mode.OrbitPlanet;
                            break;
                        case GameCamera.Mode.OrbitPlanet:
                            gameCamera.CameraMode = GameCamera.Mode.OrbitShip;
                            break;
                        case GameCamera.Mode.OrbitShip:
                            gameCamera.CameraMode = GameCamera.Mode.Fly;
                            break;
                    }
                }

                // Target switch
                if (NextTargetPlanet != Entity.Null)
                {
                    switch (gameCamera.CameraMode)
                    {
                        case GameCamera.Mode.OrbitPlanet:
                            gameCamera.FollowedEntity = NextTargetPlanet;
                            break;
                        case GameCamera.Mode.OrbitShip:
                            gameCamera.FollowedEntity = NextTargetShip;
                            break;
                    }
                }

                switch (gameCamera.CameraMode)
                {
                    case GameCamera.Mode.Fly:
                    {
                        // Yaw
                        float yawAngleChange = CameraInputs.Look.x * gameCamera.FlyRotationSpeed;
                        quaternion yawRotation = quaternion.Euler(math.up() * math.radians(yawAngleChange));
                        gameCamera.PlanarForward = math.mul(yawRotation, gameCamera.PlanarForward);

                        // Pitch
                        gameCamera.PitchAngle += -CameraInputs.Look.y * gameCamera.FlyRotationSpeed;
                        gameCamera.PitchAngle = math.clamp(gameCamera.PitchAngle, gameCamera.MinVAngle,
                            gameCamera.MaxVAngle);
                        quaternion pitchRotation = quaternion.Euler(math.right() * math.radians(gameCamera.PitchAngle));

                        // Final rotation
                        quaternion targetRotation =
                            math.mul(quaternion.LookRotationSafe(gameCamera.PlanarForward, math.up()), pitchRotation);
                        transform.Rotation = math.slerp(transform.Rotation, targetRotation,
                            MathUtilities.GetSharpnessInterpolant(gameCamera.FlyRotationSharpness, DeltaTime));

                        // Move
                        float3 worldMoveInputs = math.rotate(transform.Rotation, CameraInputs.Move);
                        float finalMaxSpeed = gameCamera.FlyMaxMoveSpeed;
                        if (CameraInputs.Sprint)
                        {
                            finalMaxSpeed *= gameCamera.FlySprintSpeedBoost;
                        }

                        gameCamera.CurrentMoveVelocity = math.lerp(gameCamera.CurrentMoveVelocity,
                            worldMoveInputs * finalMaxSpeed,
                            MathUtilities.GetSharpnessInterpolant(gameCamera.FlyMoveSharpness, DeltaTime));
                        transform.Position += gameCamera.CurrentMoveVelocity * DeltaTime;

                        break;
                    }
                    case GameCamera.Mode.OrbitPlanet:
                    case GameCamera.Mode.OrbitShip:
                    {
                        // if there is a followed entity, place the camera relatively to it
                        if (LocalToWorldLookup.TryGetComponent(gameCamera.FollowedEntity, out LocalToWorld followedLTW))
                        {
                            // Rotation
                            {
                                transform.Rotation = quaternion.LookRotationSafe(gameCamera.PlanarForward, math.up());

                                // Yaw
                                float yawAngleChange = CameraInputs.Look.x * gameCamera.OrbitRotationSpeed;
                                quaternion yawRotation = quaternion.Euler(math.up() * math.radians(yawAngleChange));
                                gameCamera.PlanarForward = math.rotate(yawRotation, gameCamera.PlanarForward);

                                // Pitch
                                gameCamera.PitchAngle += -CameraInputs.Look.y * gameCamera.OrbitRotationSpeed;
                                gameCamera.PitchAngle = math.clamp(gameCamera.PitchAngle, gameCamera.MinVAngle,
                                    gameCamera.MaxVAngle);
                                quaternion pitchRotation =
                                    quaternion.Euler(math.right() * math.radians(gameCamera.PitchAngle));

                                // Final rotation
                                transform.Rotation = quaternion.LookRotationSafe(gameCamera.PlanarForward, math.up());
                                transform.Rotation = math.mul(transform.Rotation, pitchRotation);
                            }

                            float3 cameraForward = math.mul(transform.Rotation, math.forward());

                            // Distance input
                            float desiredDistanceMovementFromInput =
                                CameraInputs.Zoom * gameCamera.OrbitDistanceMovementSpeed;
                            gameCamera.OrbitTargetDistance =
                                math.clamp(gameCamera.OrbitTargetDistance + desiredDistanceMovementFromInput,
                                    gameCamera.OrbitMinDistance, gameCamera.OrbitMaxDistance);
                            gameCamera.CurrentDistanceFromMovement = math.lerp(gameCamera.CurrentDistanceFromMovement,
                                gameCamera.OrbitTargetDistance,
                                MathUtilities.GetSharpnessInterpolant(gameCamera.OrbitDistanceMovementSharpness,
                                    DeltaTime));

                            // Calculate final camera position from targetposition + rotation + distance
                            transform.Position = followedLTW.Position +
                                                 (-cameraForward * gameCamera.CurrentDistanceFromMovement);
                        }

                        break;
                    }
                    case GameCamera.Mode.None:
                        break;
                }

                // Manually calculate the LocalToWorld since this is updating after the Transform systems, and the LtW is what rendering uses
                LocalToWorld cameraLocalToWorld = new LocalToWorld();
                cameraLocalToWorld.Value = new float4x4(transform.Rotation, transform.Position);
                LocalToWorldLookup[entity] = cameraLocalToWorld;
            }
        }
    }
}