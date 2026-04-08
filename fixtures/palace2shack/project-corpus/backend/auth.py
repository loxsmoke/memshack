JWT_SECRET = "not-a-real-secret"


def issue_tokens(user_id: str) -> dict[str, str]:
    """Issue short-lived access tokens and refresh cookies for the API."""
    return {
        "access": f"jwt-{user_id}",
        "refresh": f"refresh-{user_id}",
    }


def validate_refresh_cookie(cookie_value: str) -> bool:
    """The backend auth flow keeps refresh tokens in HttpOnly cookies."""
    return cookie_value.startswith("refresh-")
