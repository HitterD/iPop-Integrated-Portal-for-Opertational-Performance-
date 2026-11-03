"""Generates the iPop Grafana dashboards as ConfigMaps (Grafana's sidecar loads them into folder "iPop").
Run after editing:  python deploy/k8s/dashboards/make_dashboards.py
Edit here, not in the Grafana UI: provisioned dashboards are overwritten on every sync."""
import json
import pathlib

import yaml

OUT = pathlib.Path(__file__).parent
PROM = {"type": "prometheus", "uid": "prometheus"}
LOKI = {"type": "loki", "uid": "loki"}


class Board:
    def __init__(self):
        self.panels, self.y, self._id = [], 0, 0

    def _nid(self):
        self._id += 1
        return self._id

    def row(self, title):
        self.panels.append({"type": "row", "title": title, "id": self._nid(), "collapsed": False,
                            "gridPos": {"h": 1, "w": 24, "x": 0, "y": self.y}, "panels": []})
        self.y += 1

    def stat(self, title, expr, x, w=4, h=4, unit="none", steps=None, mappings=None, desc="", decimals=None):
        d = {"type": "stat", "title": title, "description": desc, "id": self._nid(), "datasource": PROM,
             "gridPos": {"h": h, "w": w, "x": x, "y": self.y},
             "targets": [{"refId": "A", "expr": expr, "instant": True}],
             "options": {"reduceOptions": {"calcs": ["lastNotNull"]}, "colorMode": "background",
                         "graphMode": "none", "textMode": "value", "justifyMode": "center"},
             "fieldConfig": {"defaults": {"unit": unit,
                                          "thresholds": {"mode": "absolute",
                                                         "steps": steps or [{"color": "green", "value": None}]},
                                          "mappings": mappings or []}, "overrides": []}}
        if decimals is not None:
            d["fieldConfig"]["defaults"]["decimals"] = decimals
        self.panels.append(d)

    def ts(self, title, targets, x, w=12, h=8, unit="none", desc="", maximum=None):
        d = {"type": "timeseries", "title": title, "description": desc, "id": self._nid(), "datasource": PROM,
             "gridPos": {"h": h, "w": w, "x": x, "y": self.y},
             "targets": [{"refId": chr(65 + i), "expr": e, "legendFormat": l} for i, (e, l) in enumerate(targets)],
             "options": {"legend": {"displayMode": "list", "placement": "bottom"}, "tooltip": {"mode": "multi"}},
             "fieldConfig": {"defaults": {"unit": unit, "custom": {"fillOpacity": 10, "lineWidth": 2}}, "overrides": []}}
        if maximum is not None:
            d["fieldConfig"]["defaults"].update({"min": 0, "max": maximum})
        self.panels.append(d)

    def logs(self, title, expr, h=10):
        self.panels.append({"type": "logs", "title": title, "id": self._nid(), "datasource": LOKI,
                            "gridPos": {"h": h, "w": 24, "x": 0, "y": self.y},
                            "targets": [{"refId": "A", "expr": expr}],
                            "options": {"showTime": True, "wrapLogMessage": True, "sortOrder": "Descending",
                                        "enableLogDetails": True}})

    def next(self, h):
        self.y += h


def g(*levels):
    """Threshold steps: g(("red", None), ("green", 1))."""
    return [{"color": c, "value": v} for c, v in levels]


UPDOWN = [{"type": "value", "options": {"0": {"text": "MATI", "color": "red"},
                                         "1": {"text": "JALAN", "color": "green"}}}]
NS = 'namespace="ipop"'


def overview():
    b = Board()
    b.row("Status sekarang (hijau = normal)")
    b.stat("iPop API", f'clamp_max(sum(kube_deployment_status_replicas_available{{{NS},deployment="ipop-api"}}), 1)', 0,
           steps=g(("red", None), ("green", 1)), mappings=UPDOWN,
           desc="Satu pod API melayani semua. MATI lebih dari 3 menit = email peringatan.")
    b.stat("iPop Web (pod siap)", f'sum(kube_deployment_status_replicas_available{{{NS},deployment="ipop-web"}})', 4,
           steps=g(("red", None), ("orange", 1), ("green", 2)), desc="Normal 2.")
    b.stat("Database (instance hidup)", f'sum(cnpg_collector_up{{{NS}}})', 8,
           steps=g(("red", None), ("orange", 1), ("green", 2)), desc="Normal 2: primary + replika.")
    b.stat("Node cluster siap", 'sum(kube_node_status_condition{condition="Ready",status="true"})', 12,
           steps=g(("red", None), ("orange", 2), ("green", 3)),
           desc="Normal 3. Dengan 2 node iPop tetap jalan; tinggal 1 = cluster berhenti.")
    b.stat("Alert aktif", 'count(ALERTS{alertstate="firing",severity=~"warning|critical"}) or vector(0)', 16,
           steps=g(("green", None), ("orange", 1), ("red", 3)), desc="Detail: menu Alerting › Alert rules.")
    b.stat("Base backup DB terakhir", f'time() - max(cnpg_collector_last_available_backup_timestamp{{{NS}}})', 20,
           unit="s", steps=g(("green", None), ("orange", 93600), ("red", 129600)),
           desc="Backup penuh ke NAS (S3) tiap 00:30. Hijau < 26 jam.")
    b.next(4)
    b.stat("Transaksi terakhir di NAS", f'max(cnpg_pg_stat_archiver_seconds_since_last_archival{{{NS}}})', 0,
           unit="s", steps=g(("green", None), ("orange", 300), ("red", 900)),
           desc="Detik sejak transaksi terakhir dikirim ke NAS (backup terus-menerus). Normal < 2 menit.")
    b.stat("Gagal kirim backup (1 jam)",
           f'max(increase(cnpg_pg_stat_archiver_failed_count{{{NS}}}[1h])) or vector(0)', 4, decimals=0,
           steps=g(("green", None), ("red", 1)), desc="Harus 0.")
    b.stat("Replika tertinggal", f'max(cnpg_pg_replication_lag{{{NS}}}) or vector(0)', 8, unit="s",
           steps=g(("green", None), ("orange", 30), ("red", 300)))
    b.stat("Ukuran database", f'max(cnpg_pg_database_size_bytes{{{NS},datname="ipop"}})', 12, unit="bytes")
    b.stat("Koneksi DB", f'sum(cnpg_backends_total{{{NS}}})', 16,
           steps=g(("green", None), ("orange", 150), ("red", 190)), desc="Maksimal 200.")
    b.stat("Restart pod (24 jam)",
           f'sum(increase(kube_pod_container_status_restarts_total{{{NS}}}[24h])) or vector(0)', 20, decimals=0,
           steps=g(("green", None), ("orange", 1), ("red", 5)),
           desc="Lebih dari 0: lihat panel log di bawah.")
    b.next(4)

    b.row("Trafik iPop")
    svc = 'exported_service=~"ipop-ipop-.*",code!="0"'
    b.ts("Request per detik (per kode HTTP)",
         [(f'sum by (code) (rate(traefik_service_requests_total{{{svc}}}[2m]))', "{{code}}")], 0, unit="reqps")
    b.ts("Error 5xx (%)",
         [(f'100 * sum(rate(traefik_service_requests_total{{{svc},code=~"5.."}}[5m])) / '
           f'clamp_min(sum(rate(traefik_service_requests_total{{{svc}}}[5m])), 0.001)', "5xx")],
         12, unit="percent", desc="Normal mendekati 0.")
    b.next(8)
    api = 'exported_service=~"ipop-ipop-.*",protocol="http"'
    b.ts("Waktu respon API",
         [(f'histogram_quantile(0.5, sum by (le) (rate(traefik_service_request_duration_seconds_bucket{{{api}}}[5m])))', "p50"),
          (f'histogram_quantile(0.95, sum by (le) (rate(traefik_service_request_duration_seconds_bucket{{{api}}}[5m])))', "p95")],
         0, unit="s", desc="Semua request http ke iPop (tanpa websocket chat realtime).")
    b.ts("Koneksi terbuka (chat/notifikasi realtime)",
         [('sum(traefik_open_connections{entrypoint="web"})', "koneksi")], 12)
    b.next(8)

    b.row("Server (3 node)")
    b.ts("CPU per node", [('100 * (1 - avg by (instance) (rate(node_cpu_seconds_total{mode="idle"}[5m])))', "{{instance}}")],
         0, w=8, unit="percent", maximum=100)
    b.ts("RAM terpakai per node",
         [('100 * (1 - node_memory_MemAvailable_bytes / node_memory_MemTotal_bytes)', "{{instance}}")],
         8, w=8, unit="percent", maximum=100)
    b.ts("Disk terpakai per node",
         [('100 * (1 - node_filesystem_avail_bytes{mountpoint="/",fstype!~"tmpfs|overlay"} / '
           'node_filesystem_size_bytes{mountpoint="/",fstype!~"tmpfs|overlay"})', "{{instance}}")],
         16, w=8, unit="percent", maximum=100, desc="Alert email di 85% dan 93%.")
    b.next(8)

    b.row("Aplikasi iPop")
    b.ts("CPU per komponen",
         [(f'sum by (container) (rate(container_cpu_usage_seconds_total{{{NS},container!="",container!="POD"}}[5m]))', "{{container}}")],
         0, unit="cores")
    b.ts("RAM per komponen",
         [(f'sum by (container) (container_memory_working_set_bytes{{{NS},container!="",container!="POD"}})', "{{container}}")],
         12, unit="bytes")
    b.next(8)
    b.ts("Volume data terpakai",
         [('100 * longhorn_volume_actual_size_bytes / longhorn_volume_capacity_bytes', "{{pvc}}")],
         0, unit="percent", maximum=100, desc="ipop-data = upload iPop; onlyoffice-data = dokumen ONLYOFFICE.")
    b.ts("Transaksi DB per detik",
         [(f'sum(rate(cnpg_pg_stat_database_xact_commit{{{NS},datname="ipop"}}[5m]))', "commit"),
          (f'sum(rate(cnpg_pg_stat_database_xact_rollback{{{NS},datname="ipop"}}[5m]))', "rollback")],
         12, unit="ops")
    b.next(8)

    b.row("Log (tersimpan 14 hari)")
    b.logs("Error & peringatan iPop API (kosong = bagus)", '{namespace="ipop", container="api"} |~ "^(fail|crit|warn): "')
    b.next(10)
    b.logs("Backup ke NAS", '{namespace="ipop", container=~"backup|plugin-barman-cloud"}', h=8)
    b.next(8)
    return {"title": "iPop – Ringkasan", "uid": "ipop-overview", "tags": ["ipop"], "timezone": "Asia/Jakarta",
            "refresh": "30s", "time": {"from": "now-6h", "to": "now"}, "schemaVersion": 39, "editable": True,
            "panels": b.panels,
            "links": [{"title": "Argo CD", "type": "link", "url": "https://argocd-ipop.santos.co.id", "targetBlank": True},
                      {"title": "Longhorn", "type": "link", "url": "https://longhorn-ipop.santos.co.id", "targetBlank": True}]}


def logs_board():
    b = Board()
    b.row("Cari log — ubah kata di kotak 'Cari' di atas (contoh: NIK, nama, error)")
    b.panels.append({"type": "logs", "title": "Log iPop (API + Web)", "id": b._nid(), "datasource": LOKI,
                     "gridPos": {"h": 20, "w": 24, "x": 0, "y": b.y},
                     "targets": [{"refId": "A", "expr": '{namespace="$ns", container=~"$komponen"} |~ "(?i)$cari"'}],
                     "options": {"showTime": True, "wrapLogMessage": True, "sortOrder": "Descending", "enableLogDetails": True}})
    b.next(20)
    b.ts("Jumlah baris log per komponen", [], 0, w=24, h=6)
    b.panels[-1]["datasource"] = LOKI
    b.panels[-1]["targets"] = [{"refId": "A", "expr": 'sum by (container) (count_over_time({namespace="$ns"}[1m]))', "legendFormat": "{{container}}"}]
    return {"title": "iPop – Log", "uid": "ipop-logs", "tags": ["ipop"], "timezone": "Asia/Jakarta", "refresh": "30s",
            "time": {"from": "now-1h", "to": "now"}, "schemaVersion": 39, "editable": True, "panels": b.panels,
            "templating": {"list": [
                {"name": "ns", "label": "Lingkungan", "type": "custom", "query": "ipop,ipop-staging",
                 "current": {"text": "ipop", "value": "ipop"},
                 "options": [{"text": "ipop", "value": "ipop", "selected": True}, {"text": "ipop-staging", "value": "ipop-staging", "selected": False}]},
                {"name": "komponen", "label": "Komponen", "type": "custom", "query": "api,web,postgres,livekit,egress,onlyoffice,redis",
                 "includeAll": True, "allValue": ".+", "multi": True, "current": {"text": "api", "value": ["api"]}},
                {"name": "cari", "label": "Cari", "type": "textbox", "query": "", "current": {"text": "", "value": ""}}]}}


def configmaps():
    return {"ipop-overview": overview(), "ipop-logs": logs_board()}


def configmap(name, dash):
    return {"apiVersion": "v1", "kind": "ConfigMap",
            "metadata": {"name": f"grafana-dashboard-{name}", "namespace": "monitoring",
                         "labels": {"grafana_dashboard": "1"}, "annotations": {"grafana_folder": "iPop"}},
            "data": {f"{name}.json": json.dumps(dash, ensure_ascii=False, indent=1)}}


if __name__ == "__main__":
    names = []
    for name, dash in configmaps().items():
        cm = configmap(name, dash)
        (OUT / f"{name}.yaml").write_text(
            "# Generated by make_dashboards.py — edit that file, then re-run it.
"
            + yaml.safe_dump(cm, allow_unicode=True, sort_keys=False, width=1000), encoding="utf-8", newline="
")
        names.append(name)
        print(name, "panels:", len(dash["panels"]))
    (OUT / "kustomization.yaml").write_text(
        "apiVersion: kustomize.config.k8s.io/v1beta1
kind: Kustomization
resources:
"
        + "".join(f"  - {n}.yaml
" for n in names), encoding="utf-8", newline="
")
