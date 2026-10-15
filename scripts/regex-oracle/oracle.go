// Compiles each pattern with Go's regexp package, which is RE2: the engine the Go SDK's validator is built on.
package main

import (
	"encoding/json"
	"fmt"
	"os"
	"regexp"
	"runtime"
)

func main() {
	raw, err := os.ReadFile(os.Args[1])
	if err != nil {
		panic(err)
	}
	var patterns []string
	if err := json.Unmarshal(raw, &patterns); err != nil {
		panic(err)
	}
	verdicts := make([]bool, 0, len(patterns))
	for _, p := range patterns {
		_, err := regexp.Compile(p)
		verdicts = append(verdicts, err == nil)
	}
	out, _ := json.Marshal(map[string]any{"version": runtime.Version(), "verdicts": verdicts})
	fmt.Println(string(out))
}
