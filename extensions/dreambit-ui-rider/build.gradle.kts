plugins {
    java
    id("org.jetbrains.intellij.platform") version "2.18.1"
}

group = "com.sondirn.dreambit"
version = "0.1.0"

repositories {
    mavenCentral()
    intellijPlatform {
        defaultRepositories()
    }
}

dependencies {
    intellijPlatform {
        rider("2026.1.4") {
            useInstaller = false
        }
        bundledPlugin("com.intellij.css")
    }
}

java {
    toolchain {
        languageVersion = JavaLanguageVersion.of(21)
    }
}

intellijPlatform {
    pluginConfiguration {
        id = "com.sondirn.dreambit.ui"
        name = "Dreambit UI"
        version = project.version.toString()

        ideaVersion {
            sinceBuild = "261"
            untilBuild = "262.*"
        }
    }
}

tasks.withType<JavaCompile>().configureEach {
    options.encoding = "UTF-8"
    options.release = 21
}
