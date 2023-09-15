using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Controllers.Child;
using Controllers.Components;
using Controllers.Items;
using Gameplay;
using Gameplay.Attribute;
using Gameplay.Customize;
using Gameplay.Events;
using Gameplay.Events.Child;
using Infrastructure;
using Mirror;
using Misc;
using UnityEngine;
using Random = UnityEngine.Random;

namespace Controllers
{
    /// <summary>
    /// <c> Infantry</c> 控制英雄机器人。
    /// <br/>可控制移动、云台旋转、小陀螺、开火、请求补给。
    /// </summary>
    public class HeroStore : RobotStoreBase
    {
        // 发弹
        [Header("Fire")] public GameObject bullet;
        public Transform muzzle;

        // 底盘
        [Header("Chassis")] public List<WheelCollider> wheels = new List<WheelCollider>();

        // 云台
        [Header("PTZ")] public Transform yaw;
        public Transform pitch;


        [Header("Aimbot")] public Camera aimbotCamera;
        private readonly ToggleHelper _aimbotHelper = new ToggleHelper();

        //用于碰撞判罚的速度记录
        private readonly Queue<Vector3> _lastVelocities = new Queue<Vector3>();
        private readonly ToggleHelper _leaveSupplyHelper = new ToggleHelper();

        // 血包
        private readonly ToggleHelper _medicalHelper = new ToggleHelper();

        // 补给
        private readonly ToggleHelper _supplyHelper = new ToggleHelper();

        //转向
        private readonly ToggleHelper _turnHelper = new ToggleHelper();
        private Aimbot _aimbot;
        private GroundChassis _groundChassis;
        private Gun _gun;
        private Rigidbody _heroRd;
        private Ptz _ptz;
        private bool _supplying;
        private Vector3 LastVelocity => _lastVelocities.Dequeue();

        /// <summary>
        /// 组件初始化
        /// </summary>
        protected override void Start()
        {
            base.Start();
            if (isServer)
            {
                // 初始化组件
                _groundChassis = new GroundChassis(gameObject, wheels, yaw);
                _ptz = new Ptz(yaw, pitch, GetComponent<Rigidbody>());
                Guns.Add(new MechanicType.GunInfo
                {
                    caliber = MechanicType.CaliberType.Large,
                    type = MechanicType.GunType.Default
                });
                var gunAttribute = AttributeManager.Instance().GunAttributes(this);
                _aimbot = new Aimbot(
                    aimbotCamera,
                    gunAttribute.MaxMuzzleVelocity,
                    8e2f);
                _gun = new Gun(
                    Guns[0].caliber,
                    muzzle, SyncLaunch,
                    gunAttribute.MaxMuzzleVelocity,
                    gunAttribute.MaxHeat,
                    gunAttribute.MuzzleCoolDownRate);
                _heroRd = gameObject.GetComponent<Rigidbody>();
            }
        }

        /// <summary>
        /// 更新组件状态。
        /// </summary>
        protected override void FixedUpdate()
        {
            base.FixedUpdate();
            if (!isServer) return;
            // 执行小陀螺或云台跟随
            var reverseAngle = _groundChassis.Update(
                Time.fixedDeltaTime,
                spinning,
                CustomizeManager.Instance().Data(
                    id,
                    CustomizeProperties.Chassis.Spinning) * 0.6f,
                CustomizeManager.Instance().Data(
                    id,
                    CustomizeProperties.Chassis.SensorDrift),
                false,
                superBattery,
                CustomizeManager.Instance().Data(
                    id,
                    CustomizeProperties.Chassis.SuperBattery) * 0.6f
            );

            // TODO: 其他车
            _ptz.YawRotate(reverseAngle);
            _ptz.Update();
            // TODO: 过热反馈
            _gun.Update(Time.fixedDeltaTime);

            // 更新参数
            var gunAttribute = AttributeManager.Instance().GunAttributes(this);
            _gun.UpdateAttributes(
                gunAttribute.MaxMuzzleVelocity,
                gunAttribute.MuzzleCoolDownRate * CurrentBuff().cooling,
                gunAttribute.MaxHeat);
            _aimbot.UpdateAttribute(gunAttribute.MaxMuzzleVelocity);

            Dispatcher.Instance().SendChild(new UpdateStatus
            {
                Speed = GetComponent<Rigidbody>().velocity.magnitude > 1e-2f ? Vector3.one : Vector3.zero,
                Spinning = spinning || Mathf.Abs(reverseAngle) > 3e-2f
            }, id);
            Dispatcher.Instance().SendChild(new SpinStatus
            {
                spinning = spinning
            }, id);
            Dispatcher.Instance().SendChild(new BulletproofStatus
            {
                bulletproof = HasEffect(EffectID.Buffs.Revival)
            }, id);
            Dispatcher.Instance().SendChild(new GunLockedStatus
            {
                gunLocked = gunLocked
            }, id);
            Dispatcher.Instance().SendChild(new SuperBatteryStatus
            {
                supperBattery = superBattery
            }, id);

            // 更新数值
            Heat = _gun.Heat;
            HeatLimit = _gun.HeatLimit;
            isMoving = _heroRd.velocity.magnitude > 0;
            if (isMoving)
            {
                movingEndTime = NetworkTime.time;
            }

            //动态记录5帧的速度
            if (_lastVelocities.Count > 5)
            {
                _lastVelocities.Dequeue();
            }
            else
            {
                _lastVelocities.Enqueue(_groundChassis.Rigidbody.velocity);
            }
            
            Power = _heroRd.velocity.magnitude * 10f;
        }

        /// <summary>
        /// 转发底盘碰撞事件。
        /// </summary>
        /// <param name="other"></param>
        private void OnCollisionEnter(Collision other)
        {
            if (!isServer) return;
            var armor = other.contacts[0].thisCollider.GetComponent<Armor>();
            if (armor != null)
            {
                armor.DelegateCollisionEnter(other);
            }
            else
            {
                _groundChassis.OnCollisionEnter(other);
            }

            //判断撞击事件
            if (other.gameObject.GetComponent<RobotStoreBase>() != null)
            {
                var crammer = other.rigidbody;
                var normalVelocity = Vector3.Dot(LastVelocity, crammer.transform.up);
                if (normalVelocity > 20)
                {
                    Dispatcher.Instance().Send(new Crash()
                    {
                        Camp = id.camp,
                        Role = id.role
                    });
                }
            }
        }

        /// <summary>
        /// 转发底盘碰撞事件。
        /// </summary>
        /// <param name="other"></param>
        private void OnCollisionExit(Collision other)
        {
            if (isServer) _groundChassis.OnCollisionExit(other);
        }

        /// <summary>
        /// 在编辑器中设置或生成时设置。
        /// </summary>
        protected override void Identify()
        {
        }

        /// <summary>
        /// 声明接收事件。
        /// </summary>
        /// <returns></returns>
        public override List<string> InputActions()
        {
            return base.InputActions().Concat(new List<string>
            {
                // 输入
                ActionID.Input.PrimaryAxis,
                ActionID.Input.ViewControl,
                ActionID.Input.StateControl,
            }).ToList();
        }

        /// <summary>
        /// 处理事件
        /// </summary>
        /// <param name="action"></param>
        /// <exception cref="ArgumentOutOfRangeException"></exception>
        [Server]
        public override void Receive(IAction action)
        {
            base.Receive(action);
            if (!isServer) return;
            switch (action.ActionName())
            {
                case ActionID.Input.PrimaryAxis:
                    if (health == 0) break;
                    var act = (PrimaryAxis)action;
                    if (act.Receiver == id && !BeCaught)
                    {
                        if (!_supplying)
                        {
                            var robotAttributes = AttributeManager.Instance().RobotAttributes(this);
                            _groundChassis.Move(
                                new Vector2(act.X, act.Y),
                                (spinning ? 0.5f : 0.7f)
                                * CustomizeManager.Instance().Data(
                                    id, CustomizeProperties.Chassis.Velocity)
                                * (partyCountDown > 0
                                    ? robotAttributes.MaxChassisPower / 25
                                    : robotAttributes.MaxChassisPower / 50), //补血点增益,地盘功率乘二
                                robotAttributes.MaxChassisPower / 10,
                                superBattery);
                        }
                        else
                        {
                            _groundChassis.Move(Vector2.zero);
                        }
                    }

                    break;

                case ActionID.Input.ViewControl:
                    if (health == 0) break;
                    var viewControlAction = (ViewControl)action;
                    if (viewControlAction.Receiver == id)
                    {
                        // 控制视角或锁定目标
                        _ptz.View(
                            Time.fixedDeltaTime,
                            !_aimbot.IsTracking()
                                ? new Vector2(viewControlAction.X, viewControlAction.Y)
                                : _aimbot.Update(Time.fixedDeltaTime),
                            Vector2.one);
                    }

                    break;

                // StateControl按键所控制的有除了移动及云台运动的其他功能，包括开火，补给等
                case ActionID.Input.StateControl:
                    if (health == 0)
                    {
                        spinning = false;
                        superBattery = false;
                        break;
                    }

                    var stateControlAction = (StateControl)action;
                    if (stateControlAction.Receiver == id)
                    {
                        // 开火
                        if (gunLocked == false)
                        {
                            if (stateControlAction.Fire)
                            {
                                _gun.Trigger();
                                lastHitTime = NetworkTime.time;
                            }
                            else
                            {
                                _gun.Release();
                            }
                        }

                        // 辅助瞄准
                        switch (_aimbotHelper.Toggle(stateControlAction.SecondaryFire))
                        {
                            case ToggleHelper.State.Re:
                                StartContinuousAimbotSession();
                                break;
                            case ToggleHelper.State.De:
                                _aimbot.EndSession();

                                break;
                        }

                        // 转向
                        switch (_turnHelper.Toggle(stateControlAction.FunctionD))
                        {
                            case ToggleHelper.State.Re:
                                _ptz.View(
                                    0.001f * Time.deltaTime,
                                    !_aimbot.IsTracking()
                                        ? new Vector2(9000000, 0)
                                        : _aimbot.Update(Time.fixedDeltaTime),
                                    Vector2.one);
                                break;
                            case ToggleHelper.State.De:
                                _ptz.View(
                                    0.001f * Time.deltaTime,
                                    !_aimbot.IsTracking()
                                        ? new Vector2(9000000, 0)
                                        : _aimbot.Update(Time.fixedDeltaTime),
                                    Vector2.one);
                                break;
                            case ToggleHelper.State.Hold:
                                break;
                            default:
                                throw new ArgumentOutOfRangeException();
                        }

                        // 补给
                        if (_supplyHelper.Toggle(stateControlAction.FunctionL) != ToggleHelper.State.Hold)
                        {
                            if (Effects.Count(e => e.type == EffectID.Status.AtSupply) > 0)
                            {
                                Dispatcher.Instance().Send(new AskSupply
                                {
                                    Target = id,
                                    Type = MechanicType.CaliberType.Large,
                                    isFar = false
                                });
                            }

                            //远程换弹
                            else
                                Dispatcher.Instance().Send(new AskSupply
                                {
                                    Target = id,
                                    Type = MechanicType.CaliberType.Large,
                                    isFar = true
                                });

                            _supplying = !_supplying;
                        }

                        // 离开补给区
                        if (_leaveSupplyHelper.Toggle(HasEffect(EffectID.Status.AtSupply)) == ToggleHelper.State.De)
                        {
                            if (_supplying)
                            {
                                Dispatcher.Instance().Send(new AskSupply
                                {
                                    Target = id,
                                    Type = MechanicType.CaliberType.Small,
                                });
                                _supplying = false;
                            }
                        }

                        spinning = stateControlAction.FunctionC;

                        //超级电容显示设置
                        superBattery = stateControlAction.FunctionJ;
                        if (superBattery)
                        {
                            startTime = NetworkTime.time;
                        }
                        else
                        {
                            switchEndTime = NetworkTime.time;
                        }

                        //RMUL的血包补给
                        if (EntityManager.Instance().CurrentMap() == MapType.RMUL2022)
                        {
                            if (_medicalHelper.Toggle(stateControlAction.FunctionE) == ToggleHelper.State.Re)
                            {
                                if (HasEffect(EffectID.Status.AtSupply) && !HasEffect(EffectID.Buffs.Medical))
                                {
                                    Dispatcher.Instance().Send(new AskMedical
                                    {
                                        Target = id
                                    });
                                }
                            }
                        }
                    }

                    break;
            }
        }

        /// <summary>
        /// 尝试寻找下一个目标。
        /// </summary>
        private void StartContinuousAimbotSession()
        {
            // 弹道解算与运动预测水平
            var bL = CustomizeManager.Instance().Data(
                id,
                CustomizeProperties.Aimbot.Ballistic);
            var pL = CustomizeManager.Instance().Data(
                id,
                CustomizeProperties.Aimbot.Prediction);
            _aimbot.StartSession<Armor>(
                bL switch
                {
                    0 => Aimbot.BallisticLevel.None,
                    1 => Aimbot.BallisticLevel.Parabola,
                    _ => Aimbot.BallisticLevel.None
                },
                pL switch
                {
                    0 => Aimbot.PredictionLevel.None,
                    1 => Aimbot.PredictionLevel.Vector,
                    2 => Aimbot.PredictionLevel.VectorAndPowerRune,
                    _ => Aimbot.PredictionLevel.None
                },
                /* 辅助瞄准目标识别 */
                /* TODO：搬走 */
                target =>
                {
                    Armor armor;
                    if (!(target is Armor armorComp))
                    {
                        armor = ((GameObject)target).GetComponent<Armor>();
                    }
                    else
                    {
                        armor = armorComp;
                    }

                    // 识别灵敏度
                    // TODO：失效

                    if (armor.camp == id.camp) return false;

                    if ((armor.transform.position - transform.position).magnitude > 9) return false;

                    var facing = Vector3.Dot(armor.transform.up, muzzle.forward) < -0.5f;

                    //英雄基地上装甲板自瞄
                    var facing1 = Vector3.Dot(armor.transform.forward, muzzle.forward) < -0.5f;
                    return armor.lightOn && (facing || facing1);
                },
                TryAimbotLater,
                () =>
                {
                    // _gun.Trigger();
                    // _gun.Release();
                });
        }

        /// <summary>
        /// 延迟重试索敌。
        /// </summary>
        private void TryAimbotLater()
        {
            StartCoroutine(StartDelayAimbot());
        }

        /// <summary>
        /// 具体延迟 0.2 秒重试索敌。
        /// TODO：在非打符场景是否需要禁用。
        /// </summary>
        /// <returns></returns>
        private IEnumerator StartDelayAimbot()
        {
            yield return new WaitForSeconds(0.2f);
            StartContinuousAimbotSession();
        }

        /// <summary>
        /// 在客户端和服务端同步发射子弹，
        /// </summary>
        /// <param name="velocity">发射初速</param>
        [Server]
        private bool SyncLaunch(Vector3 velocity)
        {
            if (gunLocked) return false;
            if (bullet.GetComponent<Bullet>().caliber == MechanicType.CaliberType.Small)
            {
                if (magSmall <= 0) return false;
                magSmall--;
                launchedSmall++;
            }
            else
            {
                if (magLarge <= 0) return false;
                if (HasEffect(EffectID.Buffs.HighlandCoin))
                {
                    Dispatcher.Instance().Send(new AskReturnCoin()
                    {
                        Camp = id.camp,
                        Coin = 10
                    });
                }

                magLarge--;
                launchedLarge++;
            }

            // 弹速波动
            var jitter = CustomizeManager.Instance().Data(
                id,
                CustomizeProperties.Gun.MuzzleVelocity);
            var realVelocity = velocity * (1 + Random.Range(0, jitter - 1) * (Random.Range(0, 2) == 1 ? 1 : -1));
            // 水平与垂直弹道波动
            var hbj = CustomizeManager.Instance()
                .Data(
                    id,
                    CustomizeProperties.Gun.HorizontalBallisticJitter) + .11f;
            var vbj = CustomizeManager.Instance()
                .Data(
                    id,
                    CustomizeProperties.Gun.VerticalBallisticJitter) + .12f;
            realVelocity += muzzle.right * (Random.Range(0, hbj - 1) * (Random.Range(0, 2) == 1 ? 1 : -1));
            realVelocity += muzzle.up * (Random.Range(0, vbj - 1) * (Random.Range(0, 2) == 1 ? 1 : -1));

            if (isServerOnly)
            {
                var newBullet = Instantiate(bullet, muzzle.position, Quaternion.identity);
                newBullet.GetComponent<Bullet>().owner = id;
                newBullet.GetComponent<Rigidbody>().velocity = realVelocity;
            }

            // 未考虑带客户端的 Host
            LaunchRpc(realVelocity);

            Dispatcher.Instance().SendChild(new Fire(), id);

            return true;
        }

        /// <summary>
        /// 在客户端生成弹丸。
        /// </summary>
        /// <param name="velocity"></param>
        [ClientRpc]
        private void LaunchRpc(Vector3 velocity)
        {
            var newBullet = Instantiate(bullet, muzzle.position, Quaternion.identity);
            newBullet.GetComponent<Bullet>().owner = id;
            newBullet.GetComponent<Rigidbody>().velocity = velocity;
        }

        /// <summary>
        /// 设置枪口热量值，用于 Q > 2Q0 过热扣血。
        /// </summary>
        /// <param name="gunIndex">发射机构序号</param>
        /// <param name="heat">热量值</param>
        [Server]
        protected override void SetHeat(int gunIndex, float heat)
        {
            _gun.Heat = heat;
        }
    }
}