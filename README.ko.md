# SSMS 22용 MSSQL Tool

[English](README.md) | [简体中文](README.zh-CN.md) | [繁體中文](README.zh-TW.md) | [日本語](README.ja.md) | **한국어** | [Deutsch](README.de.md) | [Français](README.fr.md) | [Español](README.es.md)

[![Install from VSIX Gallery](https://ssmsgallery.azurewebsites.net/badge/MSSQLTool.svg)](https://ssmsgallery.azurewebsites.net/extension/MSSQLTool/) [![Open VSIX Gallery](https://www.vsixgallery.com/badge/MSSQLTool.svg)](https://www.vsixgallery.com/extension/MSSQLTool)

🔥 **다운로드 100건을 넘었습니다 — 성원해 주셔서 감사합니다!**

## 링크

GitHub 저장소

https://github.com/PuLuShen/MSSQLTOOL

최신 릴리스(ZIP + VSIX)

https://github.com/PuLuShen/MSSQLTOOL/releases/latest

SSMS 갤러리(SQL Server Management Studio용)

https://ssmsgallery.azurewebsites.net/extension/MSSQLTool

VSIX Gallery(Visual Studio용)

https://www.vsixgallery.com/extension/MSSQLTool

문서(Wiki)

https://github.com/PuLuShen/MSSQLTOOL/wiki

## 설치

1. 위의 최신 릴리스에서 ZIP을 내려받아 `安装或重装.cmd`를 실행합니다(권장).
2. 또는 위의 SSMS 갤러리에서 최신 VSIX를 설치합니다.
3. 업데이트가 내장되어 있습니다. **설정 → 업데이트**에서 "SSMS 종료 시 설치"를 예약할 수 있습니다.

> **MSSQL Tool은 SSMS 22(SQL Server Management Studio)용 무료 오픈 소스 확장 프로그램입니다** ——
> SQL Server Management Studio 애드인으로서 문맥을 인식하는 SQL 자동 완성, T-SQL 서식,
> 쿼리 기록, 서버 상태 모니터링, 결과를 Excel / Google 스프레드시트 / 메일로 내보내는 기능을 제공합니다.
>
> SSMS 22(SQL Server Management Studio 22) 사용자는 SQL Prompt를 대체할 무료 플러그인을 자주 찾습니다.
> MSSQL Tool은 바로 그 목적을 위해 만들어졌습니다. SQL 자동 완성, 서식, 결과 처리를 내장한 무료 SSMS 확장으로,
> 쿼리를 더 빠르게 작성하고 SQL Server Management Studio 안에서 더 많은 결과 처리를 하려는 SQL Server 개발자와 DBA를 위한 것입니다.

> MSSQL Tool은 [Axial-SQL/AxialSqlTools](https://github.com/Axial-SQL/AxialSqlTools)(Apache-2.0)의 포크입니다. SQL 자동 완성 엔진, 서식 옵션, 설정 및 다국어 레이어를 그 위에서 다시 설계했습니다.

## 주요 특징

- **다국어 UI(8개 언어: English / 简体中文 / 繁體中文 / 日本語 / 한국어 / Deutsch / Français / Español)** —— 대부분의 SSMS 확장은 영어만 지원합니다.
- **SQL Prompt의 무료 대안**: SQL 자동 완성, T-SQL 서식, 쿼리 기록, Excel / 메일로 내보내기.
- **Axial SQL Tools 기반**(2016년부터 유지 관리), 자동 완성 엔진과 다국어 레이어를 강화.

*참고: Google 스프레드시트 내보내기와 GitHub 스크립트 동기화는 실험적 기능이며 계속 개발 중입니다.*

## 스크린샷

**스마트 SQL 자동 완성** —— 입력하는 동안 문맥에 맞는 후보를 보여 줍니다. 스키마, 테이블, 뷰, 열, 저장 프로시저, 함수, 매개 변수를 종류 아이콘, 범주 필터, 선택한 개체의 세부 정보와 함께 표시합니다.

<img src="pics/1.png" width="620" alt="SELECT * FROM 입력 중의 SQL 자동 완성 목록">

**설정** —— 기능마다 개별 옵션이 있고 변경 사항은 즉시 저장됩니다. 화면은 SSMS 언어를 따릅니다(여기서는 중국어 간체, 영어 등도 지원).

<img src="pics/2.png" width="760" alt="SQL 자동 완성 탭이 열린 MSSQL Tool 설정 창">

**도구 메뉴** —— 확장 기능의 명령 목록이며, 바로 가기가 지정된 명령은 함께 표시됩니다.

<img src="pics/3.png" width="520" alt="SSMS 도구 메뉴에 나열된 MSSQL Tool 명령">

**결과 집합 통계** —— 결과 그리드에서 셀을 선택하면 개수, 합계, 평균, 최소, 최대, 고유 개수를 보여 줍니다. 같은 패널에서 열 찾기, 값 찾기, 열 고정도 할 수 있습니다.

<img src="pics/4.png" width="820" alt="결과 그리드 옆의 MSSQL Tool 통계 패널">

## 기능

### SQL 편집

- **스마트 SQL 자동 완성:** 데이터베이스, 스키마, 테이블, 뷰, 열, 저장 프로시저, 함수, 매개 변수, 시노님, 연결된 서버 개체를 문맥에 맞게 제안합니다.
- **별칭을 따라가는 자동 완성:** `c.`를 입력하면 해당 테이블의 열만 나열하고 **열 이름만** 삽입하므로 직접 입력한 별칭이 그대로 유지됩니다.
- **UPDATE 지원:** `UPDATE t SET`은 대상의 쓰기 가능한 열을 제안하며, 대상이 뒤쪽 `FROM` 절에서 선언된 별칭이어도 동작합니다.
- **데이터 형식과 힌트:** `INT`, `NVARCHAR(50)`, `DATETIME2` 같은 형식과 `WITH (NOLOCK)` / `OPTION (RECOMPILE)` 같은 관용 표기를 적절한 위치에 제안합니다.
- **팝업 안의 함수 도움말:** 36개 이상의 내장 함수에 대해 시그니처, 한 줄 설명, 바로 복사할 수 있는 예제를 보여 줍니다.
- **확장 가능한 키워드:** 설정 → SQL 자동 완성에서 사용자 지정 키워드를 추가할 수 있습니다(한 줄에 하나).
- **자동 완성 메타데이터 캐시:** 백그라운드에서 단계별로 로드하며 진행률을 표시하고, 수동 초기화/새로 고침과 SQL Server 인증서 신뢰 옵션을 제공합니다.
- **F12 정의로 이동:** 데이터베이스 개체 정의를 열고 SQL 내 로컬 정의로도 이동합니다.
- **T-SQL 서식**(기존 사용 습관을 위해 유지. SSMS 22는 자체 서식을 제공합니다. 아래 "서식" 참조)
- **쿼리 템플릿:** 미리보기에서 직접 편집하고 Ctrl+S로 저장, 즐겨찾기, **태그**(`-- tags: 报表, 日结`)와 태그 검색(`#tag`), 선택 목록 상단의 **최근 10개**.
- **코드 조각**
- **`SELECT *`를 열 목록으로 확장**
- **현재 문 선택**
- **블록 주석 전환**
- **빠른 검색:** 여러 데이터베이스를 넘나드는 개체 검색. 대상은 개체 탐색기에서 가져오며, 스크립트 생성과 새 쿼리에서 열기를 지원합니다.
- **개체 정의 스크립팅**

### 결과 집합 도구

- **통계 패널(부동):** 결과 집합 안을 클릭하면 자동으로 열리고 바깥을 클릭하면 닫힙니다. 셀을 선택하면 해당 범위의 **개수·합계·평균·최소·최대·고유 개수**와 실제 열 이름을 표시합니다. 크기 조절과 위치 기억을 지원하며 **앞의 N개 열 고정**도 가능합니다.
- **열 검색:** 열 이름의 일부를 입력하면 해당 열로 스크롤합니다(`DDZ`가 `DDZT`로 잘못 이동하지 않습니다).
- **결과 집합 도구 창:** 같은 검색과 열별 통계를 도킹 대화 상자로 제공합니다(도구 → *결과 집합 검색 및 통계*). 결과는 클립보드로 한 번에 복사할 수 있습니다.

### 결과 처리 및 데이터 전송

- **결과 그리드를 Excel로 내보내기**
- **결과 그리드를 메일로 보내기**
- **결과 그리드를 임시 테이블로 내보내기**
- **그리드의 열 이름과 값을 INSERT, CSV, JSON, XML, HTML로 복사**
- **대량 데이터 전송**

### 모니터링과 워크플로

- **쿼리 기록**
- **서버 상태 대시보드**
- **실행 통계 요약**
- **트랜잭션 및 Always Encrypted 경고**
- **서버·데이터베이스 환경별 연결 색상**
- **밀리초 단위 쿼리 실행 시간**
- **결과 그리드 숫자 오른쪽 정렬**
- **SQL Server 버전 정보**
- **데이터 및 설정 폴더 변경**(다른 드라이브, 동기화 폴더, 휴대용 장치 등)

## 실험적 기능

동작은 하지만 설계가 아직 유동적입니다. 릴리스마다 동작이 바뀔 수 있으니 업그레이드 후 관련 설정을 다시 확인하세요.

| 기능 | 상태 |
| --- | --- |
| **Google 스프레드시트 내보내기** | 베타 — OAuth 설정과 열 매핑이 계속 변경 중입니다. |
| **GitHub 스크립트 동기화** | 베타 — 파일 단위 푸시/풀 방식이며 충돌 해결은 수동입니다. |

## 서식

SSMS 22에는 자체 T-SQL 서식 기능이 있으므로 **이 확장의 서식 기능은 더 이상 개발되지 않습니다**.
명령은 기존 사용 습관을 위해 여전히 동작하며 **도구** 메뉴(Ctrl+Shift+Alt+F)에 있지만, 새로운 서식 옵션은 추가되지 않습니다.

## 후원

<img src="pics/赞赏码.png" width="320" alt="후원 QR 코드" />

## FAQ

**MSSQL Tool은 무엇인가요?**
SQL 자동 완성, 서식, 결과 처리를 제공하는 SSMS 22용 무료 확장 프로그램입니다.

**MSSQL Tool은 무료인가요?**
예. Apache-2.0 오픈 소스입니다.

**SQL Prompt나 dbForge와 비교하면 어떤가요?**
MSSQL Tool은 무료 오픈 소스이며 GitHub 동기화와 Google 스프레드시트 내보내기를 내장합니다. SQL Prompt와 dbForge는 상용 제품입니다.
