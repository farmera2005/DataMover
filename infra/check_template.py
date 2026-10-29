"""Checks the compiled template (az bicep build) keeps Lakeflow's rules: a Send-only and a Listen-only policy on each
hub and never a namespace-wide key, TLS 1.2, and one hub per console division. Run by CI:

    az bicep build -f infra/main.bicep --outfile out/azuredeploy.json
    python infra/check_template.py out/azuredeploy.json src/Lakeflow.Contracts/Config.cs
"""
import json
import re
import sys


def main(template_path: str, config_cs: str) -> int:
    template = json.load(open(template_path))
    resources = template["resources"]
    if isinstance(resources, dict):  # symbolic names (languageVersion 2.0)
        resources = list(resources.values())
    by_type = {}
    for resource in resources:
        by_type.setdefault(resource["type"], []).append(resource)
    errors = []

    namespace = by_type["Microsoft.EventHub/namespaces"][0]
    if namespace["properties"].get("minimumTlsVersion") != "1.2":
        errors.append("the namespace must require TLS 1.2")
    if "Microsoft.EventHub/namespaces/authorizationRules" in by_type:
        errors.append("no namespace-wide shared access policy: keys belong to one hub each")

    rules = by_type.get("Microsoft.EventHub/namespaces/eventhubs/authorizationRules", [])
    for rule in rules:
        rights = rule["properties"]["rights"]
        name = rule["name"]
        expected = ["Send"] if "lakeflow-send" in str(name) else ["Listen"] if "fabric-listen" in str(name) else None
        if expected is None or rights != expected:
            errors.append(f"policy {name} has rights {rights}; only lakeflow-send (Send) and fabric-listen (Listen) are allowed")

    divisions = re.search(r"enum Division \{([^}]*)\}", open(config_cs).read()).group(1)
    console = sorted(d.strip().lower() for d in divisions.split(",") if d.strip())
    default = sorted(template["parameters"]["divisions"]["defaultValue"])
    if console != default:
        errors.append(f"hubs {default} do not match the console's divisions {console}")

    for error in errors:
        print("ERROR:", error)
    print("template checks:", "failed" if errors else "passed")
    return 1 if errors else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1], sys.argv[2]))
