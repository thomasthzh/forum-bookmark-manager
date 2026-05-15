import asyncio
import json

import httpx

from forum_bookmark_manager.clash import ClashProxyRotator, read_clash_controller_config


def test_read_clash_controller_config_from_clash_verge_yaml(tmp_path):
    config = tmp_path / "config.yaml"
    config.write_text(
        """
mixed-port: 7897
external-controller: 127.0.0.1:9097
secret: set-your-secret
""",
        encoding="utf-8",
    )

    controller = read_clash_controller_config(config)

    assert controller.controller_url == "http://127.0.0.1:9097"
    assert controller.secret == "set-your-secret"


def test_clash_proxy_rotator_selects_reachable_hk_or_sg_node():
    async def scenario():
        requests = []

        def handler(request: httpx.Request) -> httpx.Response:
            requests.append((request.method, str(request.url), request.headers.get("authorization"), request.content))
            path = request.url.path
            if path == "/proxies":
                return httpx.Response(
                    200,
                    json={
                        "proxies": {
                            "节点选择": {
                                "type": "Selector",
                                "all": ["美国LA", "香港HK-A-Gemini", "新加坡SG-HY2"],
                                "now": "美国LA",
                            },
                            "香港HK-A-Gemini": {"type": "Proxy"},
                            "新加坡SG-HY2": {"type": "Proxy"},
                        }
                    },
                )
            if path.endswith("/delay"):
                if "%E9%A6%99%E6%B8%AFHK-A-Gemini" in str(request.url):
                    return httpx.Response(503, json={"message": "timeout"})
                return httpx.Response(200, json={"delay": 88})
            if request.method == "PUT" and path.startswith("/proxies/"):
                return httpx.Response(204)
            return httpx.Response(404)

        transport = httpx.MockTransport(handler)
        rotator = ClashProxyRotator(
            controller_url="http://127.0.0.1:9097",
            secret="set-your-secret",
            proxy_group="节点选择",
            region_keywords=("香港", "HK", "新加坡", "SG"),
            client_factory=lambda: httpx.AsyncClient(transport=transport),
        )

        switched = await rotator.switch_to_available_proxy()

        assert switched is True
        put_requests = [request for request in requests if request[0] == "PUT"]
        assert put_requests
        assert json.loads(put_requests[0][3])["name"] == "新加坡SG-HY2"
        assert all(request[2] == "Bearer set-your-secret" for request in requests)

    asyncio.run(scenario())
