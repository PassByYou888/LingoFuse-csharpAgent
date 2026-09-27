del/s *.exe
del/s *.ini
del/s *.local
del/s *.identcache
del/s *.lps
del/s *.spec
del/s *.pdb
rd /q /s .\lib
rd /q /s .\mcp_configs
rd /q /s .\build
rd /q /s .\dist
rd /q /s .\__pycache__
rd /q /s .\llm_common\__pycache__
cd .\lingofuse\
call clear_.bat


