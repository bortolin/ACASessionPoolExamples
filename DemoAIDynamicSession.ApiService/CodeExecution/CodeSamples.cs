namespace DemoAIDynamicSession.ApiService.CodeExecution;

public static class CodeSamples
{
    public static IReadOnlyList<CodeSample> All { get; } =
    [
        new(
            "hello",
            "Hello sandbox",
            "Mostra che il codice gira dentro il container della sessione, non nel tuo processo.",
            """
            import platform, sys, os

            print("Hello dalla dynamic session!")
            print("Python:", sys.version.split()[0])
            print("Sistema:", platform.system(), platform.machine())
            print("Hostname:", platform.node())
            print("Utente:", os.environ.get("USER") or os.environ.get("USERNAME"))
            """),
        new(
            "state",
            "Stato della sessione",
            "Esegui più volte con lo stesso identificatore: il contatore continua da dove era rimasto.",
            """
            counter = globals().get("counter", 0) + 1
            globals()["counter"] = counter

            print(f"Esecuzioni in questa sessione: {counter}")
            """),
        new(
            "dataframe",
            "Analisi dati con pandas",
            "Il tipico caso d'uso AI: l'LLM genera codice di analisi che gira in sandbox.",
            """
            import pandas as pd

            vendite = pd.DataFrame({
                "regione": ["Nord", "Centro", "Sud", "Nord", "Sud"],
                "importo": [1200, 830, 640, 1500, 720],
            })

            print(vendite.groupby("regione")["importo"].sum().sort_values(ascending=False))
            print("Totale:", vendite["importo"].sum())
            """),
        new(
            "files",
            "File persistenti",
            "I file scritti restano disponibili nelle esecuzioni successive della stessa sessione.",
            """
            from pathlib import Path

            log = Path("demo.txt")
            righe = log.read_text().splitlines() if log.exists() else []
            righe.append(f"esecuzione #{len(righe) + 1}")
            log.write_text("\\n".join(righe))

            print(log.read_text())
            """),
        new(
            "isolation",
            "Isolamento di rete e filesystem",
            "Codice potenzialmente ostile: la sandbox contiene il danno.",
            """
            import os, subprocess

            print("Root del filesystem:", os.listdir("/"))

            try:
                print(subprocess.run(["whoami"], capture_output=True, text=True).stdout.strip())
            except Exception as ex:
                print("subprocess bloccato:", ex)
            """),
    ];
}
