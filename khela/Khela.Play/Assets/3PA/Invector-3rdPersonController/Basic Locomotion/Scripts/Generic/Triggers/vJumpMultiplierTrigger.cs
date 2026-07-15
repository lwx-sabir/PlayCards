using UnityEngine;
using System.Collections;
namespace Invector.vCharacterController
{
    public class vJumpMultiplierTrigger : MonoBehaviour
    {
        public float multiplier = 5;
        public float jumpCooldown = 0.5f;
        private float lastJumpTime = -1f;

        void OnTriggerStay(Collider other)
        {
            if (other.gameObject.CompareTag("Player"))
            {
                var motor = other.GetComponent<vThirdPersonController>();

                if (motor && motor.isGrounded && !motor.isJumping)
                {
                    if (Time.time - lastJumpTime >= jumpCooldown)
                    {
                        lastJumpTime = Time.time;
                        StartCoroutine(ApplyJumpWithMultiplier(motor));
                    }
                }
            }
        }

        IEnumerator ApplyJumpWithMultiplier(vThirdPersonController motor)
        {
            motor.SetJumpMultiplier(multiplier);
            yield return null;
            motor.Jump();
            yield return new WaitForSeconds(1f);
            motor.ResetJumpMultiplier();
        }
    }
}