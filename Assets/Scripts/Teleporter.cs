using UnityEngine;

public class Teleporter : MonoBehaviour
{
    [SerializeField] private Transform tpPoint;
    [SerializeField] private Transform user;

    public void TransportUser()
    {
        user.position = tpPoint.position;
        user.rotation = tpPoint.rotation;
    }
}
