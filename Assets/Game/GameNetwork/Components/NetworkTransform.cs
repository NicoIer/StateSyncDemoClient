using System;
using System.Collections.Generic;
using Network;
using Network.Client;
using UnityEngine;
using UnityEngine.Assertions;
using UnityEngine.Serialization;
using UnityToolkit;

namespace Game
{
    [AddComponentMenu("")]
    [DisallowMultipleComponent]
    public class NetworkTransform : NetworkComponentBehavior
    {
        private bool _networkInitialized = false;
        [SerializeField] private TransformComponent _remoteTransform; // 远程的
        [SerializeField] private TransformComponent _localTransform; // 本地的

        public static long serverTimeTicks => EntityNetworkMgr.Singleton.time.ServerTimeTicks;
        public static long rttTicks => EntityNetworkMgr.Singleton.time.RttTicks;
        public bool syncPosition => _remoteTransform.mask.HasFlag(TransformComponent.Mask.Pos);
        public bool syncRotation => _remoteTransform.mask.HasFlag(TransformComponent.Mask.Rotation);
        public bool syncScale => _remoteTransform.mask.HasFlag(TransformComponent.Mask.Scale);

        private readonly SortedList<double, TransformSnapshot>
            _snapshots = new SortedList<double, TransformSnapshot>(16);

        /// <summary>
        /// 同步间隔 仅对客户端上传服务器有效
        /// </summary>
        public float transformUploadInterval = 0.1f;


        private float _uploadTimer;

        private bool _remoteUpdated = false;

        /// <summary>
        /// 作为非权威客户端时 本地相对于远程的时间戳
        /// </summary>
        private double _localTimeline;

        protected override void OnNetworkSpawn(INetworkComponent component)
        {
            Assert.IsTrue(component is TransformComponent);
            _remoteTransform = (TransformComponent)component;
            _localTransform = _remoteTransform.DeepCopy();
            _remoteTransform.OnUpdated += OnRemoteTransformUpdated;

            // 立刻同步一次
            transform.position = _remoteTransform.pos;
            transform.rotation = _remoteTransform.rotation;
            transform.localScale = _remoteTransform.scale;
            // 完全跟上了服务器的位置
            _localTimeline = serverTimeTicks;
            _networkInitialized = true;
        }


        private void Update()
        {
            if (componentIdx == -1) return; // 未初始化
            UploadTransform(); // 只有权威客户端才可以上传
            SyncFromRemote(); // 权威客户端不需要同步
        }


        /// <summary>
        /// 从远程同步数据
        /// </summary>
        private void SyncFromRemote()
        {
            if (entityBehavior.Ownership) return; // 有权限的客户端不需要同步 以自己本地为准
            if (!_remoteUpdated) return; // 远程数据未更新 不需要同步
            // 不需要插值捏
            if (!_remoteTransform.interpolation)
            {
                SyncPosition(_remoteTransform.pos);
                SyncRotation(_remoteTransform.rotation);
                SyncScale(_remoteTransform.scale);
                _remoteUpdated = false;
                return;
            }

            // 如果没有快照 直接返回
            if (_snapshots.Count == 0)
            {
                // Should not happen
                Global.Log.Warning("No snapshot to interpolate");
                _remoteUpdated = false;
                return;
            }

            // 每帧都会更新本地时间线 去追赶远程时间线
            SnapshotInterpolation.Step(
                _snapshots,
                Time.deltaTime * TimeSpan.TicksPerSecond,
                ref _localTimeline,
                1,
                out var from,
                out var to,
                out var t);
            TransformSnapshot computed = TransformSnapshot.Interpolate(from, to, t);
            // 对于需要插值的数据 进行插值
            SyncPosition(computed.position);
            SyncRotation(computed.rotation);
            SyncScale(computed.scale);
            _remoteUpdated = false;
        }

        /// <summary>
        /// 上传本地的Transform数据到远程
        /// </summary>
        private void UploadTransform()
        {
            //有权限才能上传 因为客户端B不能上传客户端A的Transform
            if (!entityBehavior.Ownership) return;
            if (entityBehavior == null || !_networkInitialized) return;
            _uploadTimer += Time.deltaTime;
            if (!(_uploadTimer >= transformUploadInterval)) return;
            _uploadTimer = 0;

            // 如果位置发生变化 才更新
            bool changed = false;
            if (!(_localTransform.pos == transform.position))
            {
                _localTransform.pos = transform.position;
                changed |= true;
            }

            if (!(_localTransform.rotation == transform.rotation))
            {
                _localTransform.rotation = transform.rotation;
            }

            if (!(_localTransform.scale == transform.localScale))
            {
                _localTransform.scale = transform.localScale;
                changed |= true;
            }

            _localTransform.timestamp = serverTimeTicks;

            if (changed)
            {
                // NetworkLogger.Debug($"{this} UploadTransform");
                entityBehavior.SendComponentUpdate(componentIdx, _localTransform);
            }
        }

        /// <summary>
        /// 当RemoteTransform更新时
        /// </summary>
        private void OnRemoteTransformUpdated()
        {
            if (entityBehavior.Ownership) return; // 有权限的客户端不需要同步 以自己本地为准
            _remoteUpdated = true;
            if (_remoteTransform.interpolation)
            {
                // 当收到新的远程数据时 将其加入到快照中
                SnapshotInterpolation.InsertIfNotExists(
                    _snapshots,
                    SnapshotInterpolation.snapshotSettings.bufferLimit,
                    new TransformSnapshot
                    {
                        localTime = _localTimeline,
                        remoteTime = serverTimeTicks - (long)(rttTicks / 2), //我收到的时间已经落后了服务器rttTicks / 2
                        position = _remoteTransform.pos,
                        rotation = _remoteTransform.rotation,
                        scale = _remoteTransform.scale
                    }
                );
            }
        }

        private void SyncPosition(UnityEngine.Vector3 pos)
        {
            if (!_remoteTransform.mask.HasFlag(TransformComponent.Mask.Pos)) return;
            if (_remoteTransform.coordinateSpace == CoordinateSpace.World)
            {
                transform.position = pos;
                return;
            }

            if (_remoteTransform.coordinateSpace == CoordinateSpace.Local)
            {
                transform.localPosition = pos;
            }
        }

        private void SyncRotation(UnityEngine.Quaternion rotation)
        {
            if (!_remoteTransform.mask.HasFlag(TransformComponent.Mask.Rotation)) return;
            if (_remoteTransform.coordinateSpace == CoordinateSpace.World)
            {
                transform.rotation = rotation;
                return;
            }

            if (_remoteTransform.coordinateSpace == CoordinateSpace.Local)
            {
                transform.localRotation = rotation;
            }
        }

        private void SyncScale(UnityEngine.Vector3 scale)
        {
            if (!_remoteTransform.mask.HasFlag(TransformComponent.Mask.Scale)) return;
            if (_remoteTransform.coordinateSpace == CoordinateSpace.World)
            {
                transform.localScale = scale;
                return;
            }

            if (_remoteTransform.coordinateSpace == CoordinateSpace.Local)
            {
                transform.localScale = scale;
            }
        }

#if UNITY_EDITOR
        private void OnDrawGizmosSelected()
        {
            if (!_networkInitialized) return;
            if (_localTransform.mask.HasFlag(TransformComponent.Mask.Pos))
            {
                Gizmos.color = Color.red;
                if (_localTransform.mask.HasFlag(TransformComponent.Mask.Scale))
                {
                    Gizmos.DrawWireCube(_localTransform.pos, _localTransform.scale);
                }
                else
                {
                    Gizmos.DrawWireSphere(_localTransform.pos, 1f);
                }

                Gizmos.color = Color.green;
            }

            if (_remoteTransform.mask.HasFlag(TransformComponent.Mask.Pos))
            {
                Gizmos.color = Color.blue;
                if (_remoteTransform.mask.HasFlag(TransformComponent.Mask.Scale))
                {
                    Gizmos.DrawWireCube(_remoteTransform.pos, _remoteTransform.scale * 0.95f);
                }
                else
                {
                    Gizmos.DrawWireSphere(_remoteTransform.pos, 1f);
                }
            }
        }
#endif
    }
}