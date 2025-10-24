using UnityEngine;
using UnityEngine.InputSystem;

public class PlayerController : MonoBehaviour
{
    public float maxSpeed = 5.0f;
    public Vector3 currentVelocity;
    public float acceleration = 1f;
    public float jumpForce = 5.0f;
    private Rigidbody rb;

    InputAction moveAction;
    InputAction jumpAction;

    void Awake()
    {
        rb = GetComponent<Rigidbody>();
    }

    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        moveAction = InputSystem.actions.FindAction("Move");
        jumpAction = InputSystem.actions.FindAction("Jump");
    }

    public void BringToTop()
    {

    }

    // Update is called once per frame
    void Update()
    {


        var playerMove = moveAction.ReadValue<Vector2>();
        var playerJump = jumpAction.WasPressedThisFrame();

        currentVelocity = rb.linearVelocity;
        if (currentVelocity.magnitude < maxSpeed)
        {
            rb.AddForce(new Vector3(playerMove.x * (acceleration * Time.deltaTime), 0f,
                playerMove.y * (acceleration * Time.deltaTime)));
        }

        if (playerJump && IsGrounded())
        {
            rb.AddForce(new Vector3(0f, jumpForce, 0f), ForceMode.Impulse);
        }
    }

    bool IsGrounded()
    {
        return Physics.Raycast(transform.position, -Vector3.up, 0.1f);
    }
}
