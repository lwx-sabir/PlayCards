using UnityEngine;


namespace Bozo.ModularCharacters
{
    public class BoZo_MagicaClothCollider : MonoBehaviour
    {
    #if MAGICACLOTH2
        [SerializeField] Direction direction;
        [SerializeField] bool reverseDirection;
        [SerializeField] float length;
        [SerializeField] float startRadius;
        [SerializeField] float endRadius;
        [SerializeField] Vector3 center;

        private void Awake()
        {
            var col = gameObject.AddComponent<MagicaCloth2.MagicaCapsuleCollider>();

            switch (direction)
            {
                case Direction.X:
                    col.direction = MagicaCloth2.MagicaCapsuleCollider.Direction.X;
                    break;
                case Direction.Y:
                    col.direction = MagicaCloth2.MagicaCapsuleCollider.Direction.Y;
                    break;
                case Direction.Z:
                    col.direction = MagicaCloth2.MagicaCapsuleCollider.Direction.Z;
                    break;
                default:
                    break;
            }

            col.reverseDirection = reverseDirection;
            col.SetSize(startRadius, endRadius, length);
            col.center = center;
            col.UpdateParameters();


        }

        private enum Direction { X, Y, Z }
#endif

        private void Start()
        {
            Destroy(this);
        }

    }

}
